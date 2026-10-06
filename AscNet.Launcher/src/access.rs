//! The game runs unelevated; only its persistent document directory gets a user grant.
use anyhow::{bail, ensure, Context, Result};
use std::{fs::{self, File, OpenOptions}, os::windows::{ffi::OsStringExt, fs::{MetadataExt, OpenOptionsExt}, io::AsRawHandle}, path::{Component, Path, PathBuf}};
use windows::{core::PWSTR, Win32::{Foundation::*, Security::{*, Authorization::*}, Storage::FileSystem::*, System::{SystemServices::{ACCESS_ALLOWED_ACE_TYPE, MAXIMUM_ALLOWED}, Threading::*}}};

const DOCUMENT: &str = "PGR_Data/StreamingAssets/document";
const MODIFY: u32 = FILE_GENERIC_READ.0 | FILE_GENERIC_WRITE.0 | FILE_GENERIC_EXECUTE.0 | DELETE.0;

struct Handle(HANDLE);
impl Drop for Handle {
    fn drop(&mut self) { unsafe { let _ = CloseHandle(self.0); } }
}
struct LocalAllocation(*mut std::ffi::c_void);
impl Drop for LocalAllocation {
    fn drop(&mut self) { unsafe { let _ = LocalFree(HLOCAL(self.0)); } }
}
fn handle(file: &File) -> HANDLE { HANDLE(file.as_raw_handle() as isize) }

fn creation_time(process: HANDLE) -> Result<u64> {
    let (mut created, mut exited, mut kernel, mut user) = (FILETIME::default(), FILETIME::default(), FILETIME::default(), FILETIME::default());
    unsafe { GetProcessTimes(process, &mut created, &mut exited, &mut kernel, &mut user)?; }
    Ok((u64::from(created.dwHighDateTime) << 32) | u64::from(created.dwLowDateTime))
}

/// An authenticated process reference, not a user-selected SID.
pub fn caller_identity() -> Result<(u32, u64)> {
    unsafe { Ok((GetCurrentProcessId(), creation_time(GetCurrentProcess())?)) }
}

fn process_token(process: HANDLE) -> Result<Handle> {
    let mut token = HANDLE::default();
    unsafe { OpenProcessToken(process, TOKEN_QUERY | TOKEN_DUPLICATE, &mut token)?; }
    Ok(Handle(token))
}

fn token_user(token: HANDLE) -> Result<Vec<usize>> {
    let mut size = 0;
    unsafe { let _ = GetTokenInformation(token, TokenUser, None, 0, &mut size); }
    ensure!(size >= std::mem::size_of::<TOKEN_USER>() as u32, "caller token has no user SID");
    let mut user = vec![0usize; (size as usize).div_ceil(std::mem::size_of::<usize>())];
    unsafe { GetTokenInformation(token, TokenUser, Some(user.as_mut_ptr().cast()), size, &mut size)?; }
    Ok(user)
}

fn file_information(file: &File) -> Result<BY_HANDLE_FILE_INFORMATION> {
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    unsafe { GetFileInformationByHandle(handle(file), &mut info)?; }
    Ok(info)
}

fn open_pinned(path: &Path, access: u32) -> Result<File> {
    let file = OpenOptions::new().access_mode(access).share_mode(FILE_SHARE_READ.0)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
        .open(path).with_context(|| format!("opening document access path {}", path.display()))?;
    let info = file_information(&file)?;
    ensure!(info.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT.0 == 0,
        "refusing document access through reparse point: {}", path.display());
    ensure!(info.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY.0 != 0 || info.nNumberOfLinks == 1,
        "refusing multiply-linked document file: {}", path.display());
    Ok(file)
}

fn document_path(client: &Path) -> Result<PathBuf> {
    let client = std::path::absolute(client)?;
    ensure!(!client.components().any(|c| matches!(c, Component::ParentDir)), "unsafe game path");
    Ok(client.join(DOCUMENT))
}

// Retain read handles without write/delete sharing to prevent junction conversion
// and ancestor renames. Do not request Modify on any ancestor.
fn pin_ancestors(path: &Path) -> Result<Vec<File>> {
    let mut ancestors: Vec<_> = path.parent().context("document path has no parent")?.ancestors().collect();
    ancestors.reverse();
    let mut pins = Vec::with_capacity(ancestors.len());
    for ancestor in ancestors {
        // Ancestors above the game can be Proton bind mounts. The document tree
        // itself still goes through open_pinned and rejects every reparse point.
        let metadata = fs::symlink_metadata(ancestor)
            .with_context(|| format!("opening document access path {}", ancestor.display()))?;
        let attributes = metadata.file_attributes();
        // The Wine result is taken before the share-read pin. The pin denies a later open.
        let wine_mount = attributes & FILE_ATTRIBUTE_REPARSE_POINT.0 != 0
            && crate::install::wine_mount_keeps_its_path(ancestor);
        ensure!(
            attributes & FILE_ATTRIBUTE_REPARSE_POINT.0 == 0 || wine_mount,
            "refusing document access through reparse point: {}",
            ancestor.display()
        );
        let file = OpenOptions::new().access_mode(FILE_GENERIC_READ.0).share_mode(FILE_SHARE_READ.0)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS.0 | FILE_FLAG_OPEN_REPARSE_POINT.0)
            .open(ancestor).with_context(|| format!("opening document access path {}", ancestor.display()))?;
        // The handle is the object that was pinned. A directory swapped for a junction
        // after the path check still has the directory attribute, so check this handle too.
        let pinned = file.metadata()?.file_attributes();
        if !crate::install::directory_pin_allows(pinned, wine_mount) {
            ensure!(pinned & FILE_ATTRIBUTE_DIRECTORY.0 != 0, "document ancestor is not a directory");
            bail!("refusing document access through reparse point: {}", ancestor.display());
        }
        pins.push(file);
    }
    Ok(pins)
}

fn append_children(file: &File, path: &Path, paths: &mut Vec<PathBuf>) -> Result<()> {
    // Enumerate the pinned handle itself: FindFirstFile/read_dir opens a second
    // directory handle whose sharing can conflict with our Modify/DELETE rights.
    let mut buffer = [0u64; 8192];
    let bytes = std::mem::size_of_val(&buffer);
    loop {
        match unsafe { GetFileInformationByHandleEx(handle(file), FileIdBothDirectoryInfo,
            buffer.as_mut_ptr().cast(), bytes as u32) } {
            Ok(()) => (),
            Err(error) if error.code() == windows::core::HRESULT::from_win32(ERROR_NO_MORE_FILES.0) => return Ok(()),
            Err(error) => return Err(error).with_context(|| format!("enumerating pinned directory {}", path.display())),
        }
        let mut offset = 0;
        loop {
            ensure!(offset + std::mem::size_of::<FILE_ID_BOTH_DIR_INFO>() <= bytes,
                "invalid directory record size");
            let entry = unsafe { &*(buffer.as_ptr().cast::<u8>().add(offset).cast::<FILE_ID_BOTH_DIR_INFO>()) };
            let name_offset = offset + std::mem::offset_of!(FILE_ID_BOTH_DIR_INFO, FileName);
            ensure!(entry.FileNameLength % 2 == 0 && name_offset + entry.FileNameLength as usize <= bytes,
                "invalid directory record name");
            let name = unsafe { std::slice::from_raw_parts(buffer.as_ptr().cast::<u8>().add(name_offset).cast::<u16>(),
                entry.FileNameLength as usize / 2) };
            let name = std::ffi::OsString::from_wide(name);
            if name != "." && name != ".." {
                ensure!(Path::new(&name).components().count() == 1
                    && matches!(Path::new(&name).components().next(), Some(Component::Normal(_))),
                    "unsafe directory record name");
                paths.push(path.join(name));
            }
            if entry.NextEntryOffset == 0 { break; }
            ensure!(entry.NextEntryOffset as usize >= std::mem::size_of::<FILE_ID_BOTH_DIR_INFO>()
                && entry.NextEntryOffset % 8 == 0, "invalid directory record offset");
            offset += entry.NextEntryOffset as usize;
        }
    }
}

fn pin_tree(root: &Path, access: u32) -> Result<Vec<File>> {
    let mut paths = vec![root.to_owned()];
    let mut pins = Vec::new();
    while let Some(path) = paths.pop() {
        let file = open_pinned(&path, access)?;
        let metadata = file.metadata()?;
        ensure!(path != root || metadata.is_dir(), "game document path is not a directory");
        if metadata.is_dir() {
            append_children(&file, &path, &mut paths)?;
        } else {
            ensure!(metadata.file_attributes() & FILE_ATTRIBUTE_READONLY.0 == 0,
                "document file is read-only: {}", path.display());
        }
        pins.push(file);
    }
    Ok(pins)
}

/// Checks actual opens under the current caller's token, including existing files.
/// Missing or denied paths need consent; unsafe links and other errors fail closed.
pub fn document_writable(client: &Path) -> Result<bool> {
    let result = (|| -> Result<bool> {
        let path = document_path(client)?;
        let _ancestors = pin_ancestors(&path)?;
        let tree = pin_tree(&path, MODIFY)?;
        let token = process_token(unsafe { GetCurrentProcess() })?;
        let user = token_user(token.0)?;
        let sid = unsafe { (*(user.as_ptr() as *const TOKEN_USER)).User.Sid };
        for file in &tree {
            if file.metadata()?.is_dir() {
                let (_allocation, _, dacl) = security(file)?;
                if !has_modify(dacl, sid, SUB_CONTAINERS_AND_OBJECTS_INHERIT)? { return Ok(false); }
            }
        }
        Ok(true)
    })();
    match result {
        Ok(writable) => Ok(writable),
        Err(error) if error.chain().any(|cause| cause.downcast_ref::<std::io::Error>().is_some_and(|e|
            matches!(e.kind(), std::io::ErrorKind::PermissionDenied | std::io::ErrorKind::NotFound))) => Ok(false),
        Err(error) => Err(error),
    }
}

fn authenticated_caller(pid: u32, created: u64) -> Result<(Handle, Handle)> {
    ensure!(pid != unsafe { GetCurrentProcessId() }, "worker cannot name itself as caller");
    let process = Handle(unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE, false, pid)? });
    ensure!(creation_time(process.0)? == created, "launcher caller process was replaced");
    ensure!(unsafe { WaitForSingleObject(process.0, 0) } == WAIT_TIMEOUT, "launcher caller has exited");
    let mut name = vec![0u16; 32768];
    let mut length = name.len() as u32;
    unsafe { QueryFullProcessImageNameW(process.0, PROCESS_NAME_WIN32, PWSTR(name.as_mut_ptr()), &mut length)?; }
    let caller_image = PathBuf::from(std::ffi::OsString::from_wide(&name[..length as usize]));
    let caller_file = open_pinned(&caller_image, FILE_GENERIC_READ.0)?;
    let worker_file = open_pinned(&std::env::current_exe()?, FILE_GENERIC_READ.0)?;
    let a = file_information(&caller_file)?;
    let b = file_information(&worker_file)?;
    ensure!((a.dwVolumeSerialNumber, a.nFileIndexHigh, a.nFileIndexLow) ==
        (b.dwVolumeSerialNumber, b.nFileIndexHigh, b.nFileIndexLow), "permission caller is not this launcher executable");
    let token = process_token(process.0)?;
    Ok((process, token))
}

fn security(file: &File) -> Result<(LocalAllocation, PSECURITY_DESCRIPTOR, *mut ACL)> {
    let mut descriptor = PSECURITY_DESCRIPTOR::default();
    let mut dacl = std::ptr::null_mut();
    unsafe { GetSecurityInfo(handle(file), SE_FILE_OBJECT,
        DACL_SECURITY_INFORMATION | OWNER_SECURITY_INFORMATION | GROUP_SECURITY_INFORMATION,
        None, None, Some(&mut dacl), None, Some(&mut descriptor)).ok()?; }
    Ok((LocalAllocation(descriptor.0), descriptor, dacl))
}

fn has_modify(dacl: *mut ACL, sid: PSID, inheritance: ACE_FLAGS) -> Result<bool> {
    if dacl.is_null() { return Ok(true); }
    unsafe {
        for index in 0..u32::from((*dacl).AceCount) {
            let mut raw = std::ptr::null_mut();
            GetAce(dacl, index, &mut raw)?;
            let header = &*(raw as *const ACE_HEADER);
            if u32::from(header.AceType) != ACCESS_ALLOWED_ACE_TYPE { continue; }
            let ace = &*(raw as *const ACCESS_ALLOWED_ACE);
            if u32::from(ace.Header.AceFlags) & (INHERIT_ONLY_ACE.0 | NO_PROPAGATE_INHERIT_ACE.0) == 0
                && u32::from(ace.Header.AceFlags) & inheritance.0 == inheritance.0
                && ace.Mask & MODIFY == MODIFY
                && EqualSid(PSID(std::ptr::addr_of!(ace.SidStart) as *mut _), sid).is_ok() {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

fn grant_modify(file: &File, sid: PSID) -> Result<()> {
    let (_allocation, _descriptor, dacl) = security(file)?;
    // A null DACL already allows everyone. Replacing it would revoke existing access.
    if dacl.is_null() { return Ok(()); }
    let inheritance = if file.metadata()?.is_dir() { SUB_CONTAINERS_AND_OBJECTS_INHERIT } else { NO_INHERITANCE };
    if has_modify(dacl, sid, inheritance)? { return Ok(()); }
    unsafe {
        let entry = EXPLICIT_ACCESS_W {
            grfAccessPermissions: MODIFY,
            grfAccessMode: GRANT_ACCESS,
            grfInheritance: inheritance,
            Trustee: TRUSTEE_W { TrusteeForm: TRUSTEE_IS_SID, TrusteeType: TRUSTEE_IS_USER,
                ptstrName: PWSTR(sid.0 as *mut u16), ..Default::default() },
        };
        let mut updated = std::ptr::null_mut();
        SetEntriesInAclW(Some(&[entry]), Some(dacl), &mut updated).ok()?;
        let _updated = LocalAllocation(updated.cast());
        // MAXIMUM_ALLOWED on these handles suppresses SetSecurityInfo's recursive
        // propagation. We update only the pinned, no-follow entries ourselves.
        SetSecurityInfo(handle(file), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
            PSID::default(), PSID::default(), Some(updated), None).ok()?;
    }
    Ok(())
}

fn check_modify(file: &File, token: HANDLE) -> Result<()> {
    let (_allocation, descriptor, _) = security(file)?;
    let mapping = GENERIC_MAPPING { GenericRead: FILE_GENERIC_READ.0, GenericWrite: FILE_GENERIC_WRITE.0,
        GenericExecute: FILE_GENERIC_EXECUTE.0, GenericAll: FILE_ALL_ACCESS.0 };
    let mut privileges = [0u64; 128];
    let mut size = (privileges.len() * 8) as u32;
    let mut granted = 0;
    let mut allowed = BOOL::default();
    unsafe { AccessCheck(descriptor, token, MODIFY, &mapping,
        Some(privileges.as_mut_ptr().cast()), &mut size, &mut granted, &mut allowed)?; }
    ensure!(allowed.as_bool(), "document ACL still denies the launcher user Modify access; existing deny entries were preserved");
    Ok(())
}

/// Called only by the consented worker. The grant persists across Restore because
/// this is user data, not an owned patch file. No SID is accepted from arguments.
pub fn prepare_document_for_caller(client: &Path, pid: u32, created: u64) -> Result<()> {
    let (_caller, token) = authenticated_caller(pid, created)?;
    let user = token_user(token.0)?;
    let sid = unsafe { (*(user.as_ptr() as *const TOKEN_USER)).User.Sid };
    let mut impersonation = HANDLE::default();
    unsafe { DuplicateToken(token.0, SecurityImpersonation, &mut impersonation)?; }
    let impersonation = Handle(impersonation);
    let path = document_path(client)?;
    let _ancestors = pin_ancestors(&path)?;
    match fs::create_dir(&path) {
        Ok(()) => (),
        Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => (),
        Err(error) => return Err(error).context("creating persistent game document directory"),
    }
    let tree = pin_tree(&path, MAXIMUM_ALLOWED)?;
    for file in &tree { grant_modify(file, sid)?; }
    for file in &tree { check_modify(file, impersonation.0)?; }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    // Only disposable data. Main runs this on Windows after integration lands.
    #[test]
    fn document_grant_enables_modify_without_removing_denials() -> Result<()> {
        let path = std::env::temp_dir().join(format!("ascnet-document-acl-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&path)?;
        let result = (|| -> Result<()> {
            let file = open_pinned(&path, MAXIMUM_ALLOWED)?;
            let token = process_token(unsafe { GetCurrentProcess() })?;
            let user = token_user(token.0)?;
            let sid = unsafe { (*(user.as_ptr() as *const TOKEN_USER)).User.Sid };
            let mut impersonation = HANDLE::default();
            unsafe { DuplicateToken(token.0, SecurityImpersonation, &mut impersonation)?; }
            let impersonation = Handle(impersonation);
            let (_original, _, original_acl) = security(&file)?;
            let exercise = (|| -> Result<()> {
                let mut empty = ACL::default();
                unsafe {
                    InitializeAcl(&mut empty, std::mem::size_of::<ACL>() as u32, ACL_REVISION)?;
                    SetSecurityInfo(handle(&file), SE_FILE_OBJECT,
                        DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION,
                        PSID::default(), PSID::default(), Some(&empty), None).ok()?;
                }
                ensure!(check_modify(&file, impersonation.0).is_err(), "empty DACL unexpectedly allowed Modify");
                grant_modify(&file, sid)?;
                check_modify(&file, impersonation.0)?;
                let (_allocation, _, acl) = security(&file)?;
                let deny = EXPLICIT_ACCESS_W {
                    grfAccessPermissions: FILE_WRITE_DATA.0, grfAccessMode: DENY_ACCESS,
                    grfInheritance: NO_INHERITANCE,
                    Trustee: TRUSTEE_W { TrusteeForm: TRUSTEE_IS_SID, TrusteeType: TRUSTEE_IS_USER,
                        ptstrName: PWSTR(sid.0 as *mut u16), ..Default::default() },
                };
                let mut denied = std::ptr::null_mut();
                unsafe { SetEntriesInAclW(Some(&[deny]), Some(acl), &mut denied).ok()?; }
                let _denied = LocalAllocation(denied.cast());
                unsafe { SetSecurityInfo(handle(&file), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                    PSID::default(), PSID::default(), Some(denied), None).ok()?; }
                grant_modify(&file, sid)?;
                ensure!(check_modify(&file, impersonation.0).is_err(), "grant removed an existing denial");
                Ok(())
            })();
            // Restore while the privileged handle remains open, even on errors.
            unsafe { SetSecurityInfo(handle(&file), SE_FILE_OBJECT, DACL_SECURITY_INFORMATION,
                PSID::default(), PSID::default(), Some(original_acl), None).ok()?; }
            exercise
        })();
        let cleanup = fs::remove_dir(&path);
        result?;
        cleanup?;
        Ok(())
    }

    #[test]
    fn document_preflight_covers_new_children() -> Result<()> {
        let client = std::env::temp_dir().join(format!("ascnet-document-tree-{}", uuid::Uuid::new_v4()));
        let document = client.join(DOCUMENT);
        fs::create_dir_all(document.parent().unwrap())?;
        let exercise = (|| -> Result<()> {
            ensure!(!document_writable(&client)?, "missing document path passed preflight");
            let token = process_token(unsafe { GetCurrentProcess() })?;
            let user = token_user(token.0)?;
            let sid = unsafe { (*(user.as_ptr() as *const TOKEN_USER)).User.Sid };
            {
                let ancestors = pin_ancestors(&document)?;
                fs::create_dir(&document)?;
                let tree = pin_tree(&document, MAXIMUM_ALLOWED)?;
                for file in &tree { grant_modify(file, sid)?; }
                drop(tree);
                drop(ancestors);
            }
            ensure!(document_writable(&client)?, "repaired document failed preflight");
            let launch = document.join("launch");
            fs::create_dir(&launch)?;
            fs::write(launch.join("resource-list"), b"disposable resource list")?;
            ensure!(document_writable(&client)?, "future document children did not inherit Modify");
            fs::write(launch.join("resource-list"), b"updated resource list")?;
            fs::remove_file(launch.join("resource-list"))?;
            Ok(())
        })();
        let cleanup = fs::remove_dir_all(&client);
        exercise?;
        cleanup?;
        Ok(())
    }
}
