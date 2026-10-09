//! Wine setup downloads the MSVC compiler and Windows SDK from the Visual Studio
//! release channel. The packages are the same ones the Build Tools installer
//! uses. VSIX payloads are zips. SDK payloads are MSI files whose bytes live in
//! external cabinets.
#![cfg_attr(not(windows), allow(dead_code))]

use anyhow::{bail, Context, Result};
use serde::Deserialize;
use serde_json::Value;
use std::{
    collections::HashMap,
    fs,
    io,
    path::{Component, Path, PathBuf},
};

const TOOL_SUFFIX: &str = ".tools.hostx64.targetx64.base";
const SDK_MSIS: &[&str] = &[
    "Universal CRT Headers Libraries and Sources-x86_en-us.msi",
    "Windows SDK for Windows Store Apps Headers-x86_en-us.msi",
    "Windows SDK for Windows Store Apps Headers OnecoreUap-x86_en-us.msi",
    "Windows SDK Desktop Headers x64-x86_en-us.msi",
    "Windows SDK OnecoreUap Headers x64-x86_en-us.msi",
    // kernel32.lib and the other core import libraries are in this package.
    // Desktop Libs only has the extra desktop libraries.
    "Windows SDK for Windows Store Apps Libs-x86_en-us.msi",
    "Windows SDK Desktop Libs x64-x86_en-us.msi",
];

#[derive(Clone, Debug)]
pub(crate) struct Payload {
    pub(crate) file_name: String,
    pub(crate) url: String,
    pub(crate) sha256: String,
}

#[derive(Debug)]
pub(crate) struct ChannelInfo {
    pub(crate) manifest_url: String,
    pub(crate) license: String,
}

#[derive(Debug)]
pub(crate) struct InstallPlan {
    pub(crate) toolset: String,
    pub(crate) sdk_version: String,
    pub(crate) vsix: Vec<Payload>,
    pub(crate) sdk_payloads: Vec<Payload>,
    pub(crate) msi_names: Vec<String>,
}

#[derive(Deserialize)]
struct ChannelFile {
    #[serde(rename = "channelItems")]
    items: Vec<ChannelItem>,
}

#[derive(Deserialize)]
struct ChannelItem {
    id: String,
    #[serde(default)]
    payloads: Vec<ChannelPayload>,
    #[serde(default, rename = "localizedResources")]
    resources: Vec<ChannelResource>,
}

#[derive(Deserialize)]
struct ChannelPayload {
    #[serde(default)]
    url: String,
}

#[derive(Deserialize)]
struct ChannelResource {
    #[serde(default)]
    language: String,
    #[serde(default)]
    license: String,
}

#[derive(Deserialize)]
struct ManifestFile {
    packages: Vec<ManifestPackage>,
}

#[derive(Deserialize)]
struct ManifestPackage {
    id: String,
    #[serde(default)]
    language: String,
    #[serde(default)]
    payloads: Vec<ManifestPayload>,
    #[serde(default)]
    dependencies: Value,
}

#[derive(Deserialize)]
struct ManifestPayload {
    #[serde(rename = "fileName", default)]
    file_name: String,
    #[serde(default)]
    url: String,
    #[serde(default)]
    sha256: String,
}

pub(crate) fn channel_info(document: &str) -> Result<ChannelInfo> {
    let channel: ChannelFile = serde_json::from_str(document).context("invalid Visual Studio channel")?;
    let item = channel
        .items
        .iter()
        .find(|item| item.id.eq_ignore_ascii_case("Microsoft.VisualStudio.Manifests.VisualStudio"))
        .context("Visual Studio channel has no manifest")?;
    let url = item.payloads.iter().map(|payload| payload.url.as_str()).find(|url| !url.is_empty()).context("Visual Studio channel manifest has no url")?;
    if !url.to_ascii_lowercase().starts_with("https://") {
        bail!("Visual Studio channel manifest URL is not HTTPS");
    }
    Ok(ChannelInfo { manifest_url: url.to_owned(), license: license_url(&channel) })
}

pub(crate) fn plan(manifest_document: &str) -> Result<InstallPlan> {
    let manifest: ManifestFile = serde_json::from_str(manifest_document).context("invalid Visual Studio manifest")?;
    let mut index: HashMap<String, Vec<usize>> = HashMap::new();
    for (offset, package) in manifest.packages.iter().enumerate() {
        index.entry(package.id.to_ascii_lowercase()).or_default().push(offset);
    }
    let toolset = newest_toolset(&manifest.packages)?;
    let tool_package = one_package(&manifest.packages, &index, &format!("microsoft.vc.{toolset}{TOOL_SUFFIX}"), false)?;
    let mut vsix = vec![checked_payload(tool_package)?];
    let headers_id = format!("microsoft.vc.{toolset}.crt.headers.base");
    let desktop_id = format!("microsoft.vc.{toolset}.crt.x64.desktop.base");
    // Desktop.base is the static CRT. rustc links msvcrt.lib, vcruntime.lib,
    // and oldnames.lib from the Store package, under lib\x64.
    let store_id = format!("microsoft.vc.{toolset}.crt.x64.store.base");
    vsix.push(checked_payload(one_package(&manifest.packages, &index, &headers_id, false)?)?);
    vsix.push(checked_payload(one_package(&manifest.packages, &index, &desktop_id, false)?)?);
    vsix.push(checked_payload(one_package(&manifest.packages, &index, &store_id, false)?)?);
    let redist_id = format!("microsoft.vc.{toolset}.crt.redist.x64.base");
    if index.contains_key(&redist_id) {
        vsix.push(checked_payload(one_package(&manifest.packages, &index, &redist_id, false)?)?);
    }
    let res_id = format!("microsoft.vc.{toolset}.tools.hostx64.targetx64.res.base");
    if index.contains_key(&res_id) {
        vsix.push(checked_payload(one_package(&manifest.packages, &index, &res_id, true)?)?);
    }

    let sdk_version = newest_sdk(&manifest.packages)?;
    let component_id = manifest
        .packages
        .iter()
        .find(|package| sdk_number(&package.id) == Some(sdk_version))
        .map(|package| package.id.as_str())
        .context("Visual Studio manifest lost the selected Windows SDK")?;
    let component = one_package(&manifest.packages, &index, component_id, false)?;
    let sdk_package_id = sdk_dependency(component)?;
    let sdk_package = one_package(&manifest.packages, &index, &sdk_package_id, false)?;
    let sdk_payloads = sdk_package
        .payloads
        .iter()
        .filter_map(|payload| payload_record(payload, &sdk_package.id).ok())
        .collect::<Vec<_>>();
    let msi_names: Vec<String> = SDK_MSIS.iter().map(|name| (*name).to_owned()).collect();
    for name in &msi_names {
        find_payload(&sdk_payloads, name)?;
    }
    Ok(InstallPlan { toolset, sdk_version: sdk_version.to_string(), vsix, sdk_payloads, msi_names })
}

pub(crate) fn find_payload<'a>(payloads: &'a [Payload], name: &str) -> Result<&'a Payload> {
    let want = base_name(name);
    payloads
        .iter()
        .find(|payload| base_name(&payload.file_name).eq_ignore_ascii_case(want))
        .with_context(|| format!("Visual Studio manifest has no payload named {want}"))
}

pub(crate) fn cabinet_names(bytes: &[u8]) -> Vec<String> {
    let mut found = Vec::new();
    let mut index = 0;
    while index + 4 <= bytes.len() {
        let Some(relative) = bytes[index..].windows(4).position(|window| window == b".cab") else { break };
        let at = index + relative;
        if at >= 32 && bytes[at - 32..at].iter().all(|byte| byte.is_ascii_hexdigit()) {
            let name = format!("{}.cab", String::from_utf8_lossy(&bytes[at - 32..at]).to_ascii_lowercase());
            if !found.iter().any(|existing: &String| existing == &name) {
                found.push(name);
            }
        }
        index = at + 4;
    }
    found
}

pub(crate) fn extract_vsix(archive: &Path, dest: &Path) -> Result<()> {
    let mut zip = zip::ZipArchive::new(fs::File::open(archive).with_context(|| format!("open {}", archive.display()))?).with_context(|| format!("open MSVC archive {}", archive.display()))?;
    let mut extracted = 0usize;
    for index in 0..zip.len() {
        let mut file = zip.by_index(index).with_context(|| format!("read MSVC archive {}", archive.display()))?;
        let name = file.name().replace('\\', "/");
        let Some(relative) = name.strip_prefix("Contents/") else { continue };
        if relative.is_empty() || name.ends_with('/') {
            if !relative.is_empty() {
                let _ = safe_join(dest, relative)?;
            }
            continue;
        }
        let output = safe_join(dest, relative)?;
        if let Some(parent) = output.parent() {
            fs::create_dir_all(parent)?;
        }
        let mut handle = fs::File::create(&output).with_context(|| format!("create {}", output.display()))?;
        io::copy(&mut file, &mut handle).with_context(|| format!("unpack {}", output.display()))?;
        extracted += 1;
    }
    if extracted == 0 {
        bail!("MSVC archive {} did not contain a Contents directory", archive.display());
    }
    Ok(())
}

pub(crate) fn extract_msi(msi_path: &Path, cab_dir: &Path, dest: &Path) -> Result<()> {
    let mut package = msi::Package::open(fs::File::open(msi_path).with_context(|| format!("open {}", msi_path.display()))?).with_context(|| format!("read MSI {}", msi_path.display()))?;
    let directories = directory_rows(&mut package)?;
    let components = component_dirs(&mut package, &directories)?;
    let disks = media_rows(&mut package)?;
    let files = file_rows(&mut package, &components)?;
    let mut groups: HashMap<String, Vec<PackedFile>> = HashMap::new();
    for file in files {
        let disk = disks
            .iter()
            .filter(|disk| disk.last_sequence >= file.sequence)
            .min_by_key(|disk| disk.last_sequence)
            .with_context(|| format!("MSI {} has no cabinet for {}", msi_path.display(), file.relative.display()))?;
        groups.entry(disk.cabinet.clone()).or_default().push(file);
    }
    for (cabinet, mut files) in groups {
        files.sort_by_key(|file| file.sequence);
        let cab_path = child_named(cab_dir, &cabinet).filter(|path| path.is_file()).with_context(|| format!("cabinet {cabinet} was not downloaded next to {}", msi_path.display()))?;
        let mut reader = cab::Cabinet::new(fs::File::open(&cab_path).with_context(|| format!("open {}", cab_path.display()))?).with_context(|| format!("read cabinet {cabinet}"))?;
        for file in files {
            let mut packed = reader.read_file(&file.id).with_context(|| format!("read {} from {cabinet}", file.relative.display()))?;
            if !keep_sdk_file(&file.relative) {
                io::copy(&mut packed, &mut io::sink()).with_context(|| format!("skip {} in {cabinet}", file.relative.display()))?;
                continue;
            }
            let output = safe_join(dest, &file.relative.to_string_lossy())?;
            if let Some(parent) = output.parent() {
                fs::create_dir_all(parent)?;
            }
            let mut handle = fs::File::create(&output).with_context(|| format!("create {}", output.display()))?;
            io::copy(&mut packed, &mut handle).with_context(|| format!("unpack {}", output.display()))?;
        }
    }
    Ok(())
}

pub(crate) fn remove_telemetry(root: &Path) {
    let Some((_, cl, _)) = find_tool_root(root) else { return };
    let Some(bin) = cl.parent() else { return };
    if let Some(telemetry) = child_named(bin, "vctip.exe") {
        let _ = fs::remove_file(telemetry);
    }
}

pub(crate) fn is_ready(root: &Path) -> bool {
    compiler_vars(root).is_ok()
}

pub(crate) fn compiler_vars(root: &Path) -> Result<Vec<(String, String)>> {
    let (tool, cl, _) = find_tool_root(root).context("MSVC layout is missing VC\\Tools\\MSVC\\*\\bin\\Hostx64\\x64\\cl.exe")?;
    let version = tool.file_name().and_then(|name| name.to_str()).context("MSVC tool directory has no version name")?.to_owned();
    require_file(child_named(&tool, "include").and_then(|dir| child_named(&dir, "vcruntime.h")), "VC include\\vcruntime.h")?;
    require_file(nested(&tool, &["lib", "x64", "libcmt.lib"]), "VC lib\\x64\\libcmt.lib")?;
    require_file(nested(&tool, &["lib", "x64", "msvcrt.lib"]), "VC lib\\x64\\msvcrt.lib")?;
    require_file(nested(&tool, &["lib", "x64", "vcruntime.lib"]), "VC lib\\x64\\vcruntime.lib")?;
    require_file(nested(&tool, &["lib", "x64", "oldnames.lib"]), "VC lib\\x64\\oldnames.lib")?;
    let kits = nested(root, &["Windows Kits", "10"]).context("MSVC layout is missing Windows Kits\\10")?;
    let include_root = child_named(&kits, "Include").context("MSVC layout is missing Windows Kits\\10\\Include")?;
    let sdk_include = highest_numbered_dir(&include_root, "10.").context("MSVC layout is missing a Windows SDK include version")?;
    let sdk_version = sdk_include.file_name().and_then(|name| name.to_str()).context("Windows SDK include directory has no version")?.to_owned();
    let lib_root = child_named(&kits, "Lib").context("MSVC layout is missing Windows Kits\\10\\Lib")?;
    let sdk_lib = child_named(&lib_root, &sdk_version).context("MSVC layout is missing the Windows SDK library version")?;
    require_file(nested(&sdk_include, &["ucrt", "corecrt.h"]), "Windows SDK ucrt\\corecrt.h")?;
    require_file(nested(&sdk_include, &["um", "windows.h"]), "Windows SDK um\\windows.h")?;
    require_file(nested(&sdk_include, &["shared", "winapifamily.h"]), "Windows SDK shared\\winapifamily.h")?;
    require_file(nested(&sdk_lib, &["um", "x64", "kernel32.lib"]), "Windows SDK um\\x64\\kernel32.lib")?;
    require_file(nested(&sdk_lib, &["ucrt", "x64", "ucrt.lib"]), "Windows SDK ucrt\\x64\\ucrt.lib")?;

    let mut include = Vec::new();
    push_dir(&mut include, child_named(&tool, "include"));
    for name in ["ucrt", "shared", "um", "winrt", "cppwinrt"] {
        push_dir(&mut include, child_named(&sdk_include, name));
    }
    let mut libs = Vec::new();
    push_dir(&mut libs, nested(&tool, &["lib", "x64"]));
    push_dir(&mut libs, nested(&sdk_lib, &["ucrt", "x64"]));
    push_dir(&mut libs, nested(&sdk_lib, &["um", "x64"]));
    let cl_dir = cl.parent().context("cl.exe has no directory")?;
    Ok(vec![
        ("Path".to_owned(), win_path(cl_dir)),
        ("INCLUDE".to_owned(), include.join(";")),
        ("LIB".to_owned(), libs.join(";")),
        ("LIBPATH".to_owned(), libs.join(";")),
        ("VCToolsInstallDir".to_owned(), win_dir(&tool)),
        ("VCToolsVersion".to_owned(), version),
        ("WindowsSdkDir".to_owned(), win_dir(&kits)),
        ("WindowsSDKVersion".to_owned(), format!("{sdk_version}\\")),
        ("VSCMD_SKIP_SENDTELEMETRY".to_owned(), "1".to_owned()),
    ])
}

fn license_url(channel: &ChannelFile) -> String {
    for item in &channel.items {
        if !item.id.eq_ignore_ascii_case("Microsoft.VisualStudio.Product.BuildTools") {
            continue;
        }
        for resource in &item.resources {
            if resource.language.eq_ignore_ascii_case("en-us") && resource.license.to_ascii_lowercase().starts_with("https://") {
                return resource.license.clone();
            }
        }
    }
    "https://go.microsoft.com/fwlink/?LinkId=2179911".to_owned()
}

fn newest_toolset(packages: &[ManifestPackage]) -> Result<String> {
    let mut best: Option<(Vec<u32>, String)> = None;
    for package in packages {
        let Some(numbers) = toolset_numbers(&package.id) else { continue };
        let label = package.id.to_ascii_lowercase();
        let label = label.strip_prefix("microsoft.vc.").and_then(|rest| rest.strip_suffix(TOOL_SUFFIX)).unwrap_or("").to_owned();
        if best.as_ref().map(|(current, _)| numbers > *current).unwrap_or(true) {
            best = Some((numbers, label));
        }
    }
    best.map(|(_, label)| label).context("Visual Studio manifest has no HostX64 TargetX64 MSVC toolset")
}

fn newest_sdk(packages: &[ManifestPackage]) -> Result<u32> {
    packages.iter().filter_map(|package| sdk_number(&package.id)).max().context("Visual Studio manifest has no Windows 10 or 11 SDK")
}

fn toolset_numbers(id: &str) -> Option<Vec<u32>> {
    let id = id.to_ascii_lowercase();
    let rest = id.strip_prefix("microsoft.vc.")?;
    let label = rest.strip_suffix(TOOL_SUFFIX)?;
    if label.contains("preview") {
        return None;
    }
    let numbers = label.split('.').map(|part| part.parse::<u32>().ok()).collect::<Option<Vec<_>>>()?;
    if numbers.is_empty() { None } else { Some(numbers) }
}

fn sdk_number(id: &str) -> Option<u32> {
    let id = id.to_ascii_lowercase();
    let rest = id
        .strip_prefix("microsoft.visualstudio.component.windows11sdk.")
        .or_else(|| id.strip_prefix("microsoft.visualstudio.component.windows10sdk."))?;
    if rest.chars().all(|character| character.is_ascii_digit()) { rest.parse().ok() } else { None }
}

fn one_package<'a>(packages: &'a [ManifestPackage], index: &HashMap<String, Vec<usize>>, id: &str, english_resources: bool) -> Result<&'a ManifestPackage> {
    let rows = index.get(&id.to_ascii_lowercase()).with_context(|| format!("Visual Studio manifest has no package {id}"))?;
    let mut chosen: Vec<&ManifestPackage> = rows
        .iter()
        .map(|offset| &packages[*offset])
        .filter(|package| {
            if !english_resources {
                return true;
            }
            package.language.eq_ignore_ascii_case("en-US") || package.payloads.iter().any(|payload| payload.file_name.to_ascii_lowercase().contains(".enu."))
        })
        .collect();
    if chosen.is_empty() {
        bail!("Visual Studio manifest has no English payload for {id}");
    }
    chosen.sort_by_key(|package| {
        let language = if package.language.eq_ignore_ascii_case("en-US") { 0 } else { 1 };
        let english_name = if package.payloads.iter().any(|payload| payload.file_name.to_ascii_lowercase().contains(".enu.")) { 0 } else { 1 };
        (language, english_name, package.payloads.first().map(|payload| payload.file_name.clone()).unwrap_or_default())
    });
    Ok(chosen[0])
}

fn sdk_dependency(component: &ManifestPackage) -> Result<String> {
    let Some(map) = component.dependencies.as_object() else {
        bail!("Windows SDK component {} has no dependencies", component.id);
    };
    let mut ids: Vec<&String> = map.keys().filter(|id| {
        let id = id.to_ascii_lowercase();
        id.starts_with("win10sdk") || id.starts_with("win11sdk")
    }).collect();
    ids.sort();
    ids.pop().cloned().with_context(|| format!("Windows SDK component {} does not name a WinSDK package", component.id))
}

fn checked_payload(package: &ManifestPackage) -> Result<Payload> {
    let payload = package
        .payloads
        .iter()
        .find(|payload| payload.file_name.to_ascii_lowercase().contains(".enu."))
        .or_else(|| package.payloads.first())
        .with_context(|| format!("Visual Studio package {} has no payload", package.id))?;
    payload_record(payload, &package.id)
}

fn payload_record(payload: &ManifestPayload, owner: &str) -> Result<Payload> {
    if !payload.url.to_ascii_lowercase().starts_with("https://") {
        bail!("Visual Studio package {owner} payload {} has no HTTPS url", payload.file_name);
    }
    let sha256 = payload.sha256.to_ascii_lowercase();
    if sha256.len() != 64 || !sha256.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        bail!("Visual Studio package {owner} payload {} has no SHA-256", payload.file_name);
    }
    Ok(Payload { file_name: payload.file_name.clone(), url: payload.url.clone(), sha256 })
}

fn base_name(name: &str) -> &str {
    name.rsplit(['/', '\\']).next().unwrap_or(name)
}

struct DirRow {
    parent: Option<String>,
    name: Option<String>,
}

struct Disk {
    last_sequence: i32,
    cabinet: String,
}

struct PackedFile {
    id: String,
    relative: PathBuf,
    sequence: i32,
}

fn directory_rows(package: &mut msi::Package<fs::File>) -> Result<HashMap<String, DirRow>> {
    let mut directories = HashMap::new();
    for row in package.select_rows(msi::Select::table("Directory")).context("MSI has no Directory table")? {
        let id = cell_str(&row, "Directory")?;
        let parent = row_str(&row, "Directory_Parent");
        let name = msi_target_name(&cell_str(&row, "DefaultDir")?);
        directories.insert(id, DirRow { parent, name });
    }
    Ok(directories)
}

fn component_dirs(package: &mut msi::Package<fs::File>, directories: &HashMap<String, DirRow>) -> Result<HashMap<String, PathBuf>> {
    let mut components = HashMap::new();
    for row in package.select_rows(msi::Select::table("Component")).context("MSI has no Component table")? {
        let id = cell_str(&row, "Component")?;
        let directory = cell_str(&row, "Directory_")?;
        components.insert(id, directory_path(directories, &directory)?);
    }
    Ok(components)
}

fn media_rows(package: &mut msi::Package<fs::File>) -> Result<Vec<Disk>> {
    let mut disks = Vec::new();
    for row in package.select_rows(msi::Select::table("Media")).context("MSI has no Media table")? {
        let Some(raw) = row_str(&row, "Cabinet") else { continue };
        if raw.starts_with('#') {
            bail!("MSI embeds cabinet {raw}; setup only unpacks external cabinets");
        }
        let cabinet = base_name(&raw).to_owned();
        if cabinet.is_empty() {
            continue;
        }
        disks.push(Disk { last_sequence: cell_i32(&row, "LastSequence")?, cabinet });
    }
    if disks.is_empty() {
        bail!("MSI did not reference an external cabinet");
    }
    Ok(disks)
}

fn file_rows(package: &mut msi::Package<fs::File>, components: &HashMap<String, PathBuf>) -> Result<Vec<PackedFile>> {
    let mut files = Vec::new();
    for row in package.select_rows(msi::Select::table("File")).context("MSI has no File table")? {
        let id = cell_str(&row, "File")?;
        let component = cell_str(&row, "Component_")?;
        let name = msi_target_name(&cell_str(&row, "FileName")?).with_context(|| format!("MSI file {id} has no destination name"))?;
        if name.contains(['/', '\\']) || name == ".." {
            bail!("MSI file {id} has an unsafe name");
        }
        let directory = components.get(&component).with_context(|| format!("MSI file {id} references unknown component {component}"))?;
        files.push(PackedFile { id, relative: directory.join(name), sequence: cell_i32(&row, "Sequence")? });
    }
    Ok(files)
}

fn directory_path(directories: &HashMap<String, DirRow>, id: &str) -> Result<PathBuf> {
    let mut names = Vec::new();
    let mut current = Some(id.to_owned());
    let mut guard = 0;
    while let Some(id) = current {
        guard += 1;
        if guard > 32 {
            bail!("MSI directory {id} is nested too deeply");
        }
        let row = directories.get(&id).with_context(|| format!("MSI directory {id} is missing"))?;
        if let Some(name) = &row.name {
            if !skipped_dir(name) {
                names.push(name.clone());
            }
        }
        current = row.parent.clone();
    }
    names.reverse();
    let mut path = PathBuf::new();
    for name in names {
        path.push(name);
    }
    Ok(path)
}

fn skipped_dir(name: &str) -> bool {
    matches!(
        name.to_ascii_lowercase().as_str(),
        "targetdir" | "sourcedir" | "program files" | "program files (x86)" | "program files (arm)" | "." | ""
    )
}

fn msi_target_name(raw: &str) -> Option<String> {
    let raw = raw.trim();
    if raw.is_empty() || raw == "." || raw == ".:" {
        return None;
    }
    let target = if let Some(rest) = raw.strip_prefix(".:") { rest } else { raw.split_once(':').map(|(target, _)| target).unwrap_or(raw) };
    let long = target.split_once('|').map(|(_, long)| long).unwrap_or(target).trim();
    if long.is_empty() || long == "." || long.contains(['/', '\\', ':']) || long == ".." {
        None
    } else {
        Some(long.to_owned())
    }
}

fn keep_sdk_file(relative: &Path) -> bool {
    let mut include = false;
    let mut lib = false;
    let mut x64 = false;
    for component in relative.components() {
        let Component::Normal(name) = component else { continue };
        let name = name.to_string_lossy();
        if name.eq_ignore_ascii_case("include") {
            include = true;
        }
        if name.eq_ignore_ascii_case("lib") {
            lib = true;
        }
        if name.eq_ignore_ascii_case("x64") {
            x64 = true;
        }
    }
    include || (lib && x64)
}

fn cell_str(row: &msi::Row, name: &str) -> Result<String> {
    row_str(row, name).with_context(|| format!("MSI column {name} is empty"))
}

fn row_str(row: &msi::Row, name: &str) -> Option<String> {
    let value = &row[name];
    value.as_str().map(str::to_owned).filter(|text| !text.is_empty())
}

fn cell_i32(row: &msi::Row, name: &str) -> Result<i32> {
    let value = &row[name];
    if let Some(number) = value.as_int() {
        return Ok(number);
    }
    if let Some(text) = value.as_str() {
        return text.parse().with_context(|| format!("MSI column {name} is not an integer"));
    }
    bail!("MSI column {name} is not an integer")
}

fn safe_join(root: &Path, relative: &str) -> Result<PathBuf> {
    let mut output = root.to_path_buf();
    if relative.is_empty() {
        bail!("archive path is empty");
    }
    for component in Path::new(&relative.replace('\\', "/")).components() {
        match component {
            Component::Normal(part) => output.push(part),
            Component::CurDir => {}
            _ => bail!("archive path escapes its destination: {relative}"),
        }
    }
    Ok(output)
}

fn child_named(dir: &Path, name: &str) -> Option<PathBuf> {
    fs::read_dir(dir).ok()?.flatten().find(|entry| entry.file_name().to_string_lossy().eq_ignore_ascii_case(name)).map(|entry| entry.path())
}

fn nested(dir: &Path, names: &[&str]) -> Option<PathBuf> {
    let mut current = dir.to_path_buf();
    for name in names {
        current = child_named(&current, name)?;
    }
    Some(current)
}

fn find_tool_root(root: &Path) -> Option<(PathBuf, PathBuf, PathBuf)> {
    let tools = nested(root, &["VC", "Tools", "MSVC"])?;
    let mut best: Option<(Vec<u32>, PathBuf, PathBuf, PathBuf)> = None;
    for entry in fs::read_dir(tools).ok()?.flatten() {
        if !entry.path().is_dir() {
            continue;
        }
        let Some(cl_dir) = nested(&entry.path(), &["bin", "Hostx64", "x64"]) else { continue };
        let Some(cl) = child_named(&cl_dir, "cl.exe") else { continue };
        let Some(link) = child_named(&cl_dir, "link.exe") else { continue };
        if !cl.is_file() || !link.is_file() {
            continue;
        }
        let numbers: Vec<u32> = entry.file_name().to_string_lossy().split('.').map(|part| part.parse().unwrap_or(0)).collect();
        if best.as_ref().map(|(current, _, _, _)| numbers > *current).unwrap_or(true) {
            best = Some((numbers, entry.path(), cl, link));
        }
    }
    best.map(|(_, tool, cl, link)| (tool, cl, link))
}

fn highest_numbered_dir(dir: &Path, prefix: &str) -> Option<PathBuf> {
    let mut best: Option<(Vec<u32>, PathBuf)> = None;
    for entry in fs::read_dir(dir).ok()?.flatten() {
        if !entry.path().is_dir() {
            continue;
        }
        let name = entry.file_name().to_string_lossy().to_string();
        if !name.starts_with(prefix) {
            continue;
        }
        let numbers: Vec<u32> = name.split('.').map(|part| part.parse().unwrap_or(0)).collect();
        if best.as_ref().map(|(current, _)| numbers > *current).unwrap_or(true) {
            best = Some((numbers, entry.path()));
        }
    }
    best.map(|(_, path)| path)
}

fn require_file(path: Option<PathBuf>, label: &str) -> Result<()> {
    match path {
        Some(path) if path.is_file() => Ok(()),
        Some(path) => bail!("MSVC layout is missing {label} ({})", path.display()),
        None => bail!("MSVC layout is missing {label}"),
    }
}

fn push_dir(list: &mut Vec<String>, path: Option<PathBuf>) {
    if let Some(path) = path {
        if path.is_dir() {
            list.push(win_path(&path));
        }
    }
}

fn win_path(path: &Path) -> String {
    path.to_string_lossy().replace('/', "\\")
}

fn win_dir(path: &Path) -> String {
    let mut text = win_path(path);
    if !text.ends_with('\\') {
        text.push('\\');
    }
    text
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Cursor, Write};

    const HASH: &str = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    fn payload(name: &str) -> String {
        format!(r#"{{"fileName":"{name}","url":"https://example.invalid/{name}","sha256":"{HASH}"}}"#)
    }

    #[test]
    fn plan_picks_the_newest_hostx64_toolset_and_sdk() {
        let channel = r#"{"channelItems":[
            {"id":"Microsoft.VisualStudio.Manifests.VisualStudio","payloads":[{"url":"https://example.invalid/VisualStudio.vsman"}]},
            {"id":"Microsoft.VisualStudio.Product.BuildTools","localizedResources":[{"language":"en-us","license":"https://example.invalid/license"}]}
        ]}"#;
        let msis = SDK_MSIS.iter().map(|name| payload(&format!("Installers/{name}"))).collect::<Vec<_>>().join(",");
        let manifest = format!(
            r#"{{"packages":[
                {{"id":"Microsoft.VC.14.29.16.11.Tools.HostX64.TargetX64.base","payloads":[{old_tool}]}},
                {{"id":"Microsoft.VC.14.44.17.14.Tools.HostX64.TargetX64.base","payloads":[{tool}]}},
                {{"id":"Microsoft.VC.14.44.17.14.CRT.Headers.base","payloads":[{headers}]}},
                {{"id":"Microsoft.VC.14.44.17.14.CRT.x64.Desktop.base","payloads":[{desktop}]}},
                {{"id":"Microsoft.VC.14.44.17.14.CRT.x64.Store.base","payloads":[{store}]}},
                {{"id":"Microsoft.VC.14.44.17.14.CRT.Redist.X64.base","payloads":[{redist}]}},
                {{"id":"Microsoft.VC.14.44.17.14.Tools.HostX64.TargetX64.Res.base","language":"de-DE","payloads":[{deu}]}},
                {{"id":"Microsoft.VC.14.44.17.14.Tools.HostX64.TargetX64.Res.base","language":"en-US","payloads":[{enu}]}},
                {{"id":"Microsoft.VisualStudio.Component.Windows10SDK.19041","dependencies":{{"Win10SDK_10.0.19041":"[10.0,11.0)"}}}},
                {{"id":"Microsoft.VisualStudio.Component.Windows11SDK.26100","dependencies":{{"Win11SDK_10.0.26100":"[10.0,11.0)"}}}},
                {{"id":"Win11SDK_10.0.26100","payloads":[{msis}]}},
                {{"id":"Win10SDK_10.0.19041","payloads":[]}}
            ]}}"#,
            old_tool = payload("old-tools.vsix"),
            tool = payload("tools.vsix"),
            headers = payload("headers.vsix"),
            desktop = payload("desktop.vsix"),
            store = payload("store.vsix"),
            redist = payload("redist.vsix"),
            deu = payload("res.deu.vsix"),
            enu = payload("res.enu.vsix"),
            msis = msis,
        );
        let info = channel_info(channel).unwrap();
        assert_eq!(info.manifest_url, "https://example.invalid/VisualStudio.vsman");
        assert_eq!(info.license, "https://example.invalid/license");
        let selected = plan(&manifest).unwrap();
        assert_eq!(selected.toolset, "14.44.17.14");
        assert_eq!(selected.sdk_version, "26100");
        let names: Vec<_> = selected.vsix.iter().map(|item| item.file_name.as_str()).collect();
        assert_eq!(names, vec!["tools.vsix", "headers.vsix", "desktop.vsix", "store.vsix", "redist.vsix", "res.enu.vsix"]);
        assert!(selected.msi_names.iter().any(|name| name.contains("Desktop Libs x64")));
        assert!(selected.msi_names.iter().any(|name| name.contains("Store Apps Libs")));
        assert!(find_payload(&selected.sdk_payloads, "Windows SDK Desktop Libs x64-x86_en-us.msi").is_ok());
        assert!(find_payload(&selected.sdk_payloads, "Windows SDK for Windows Store Apps Libs-x86_en-us.msi").is_ok());
    }

    #[test]
    fn cabinet_names_keep_only_32_hex_digits() {
        let mut bytes = b"exit.".to_vec();
        bytes.extend(b"16ab2ea2187acffa6435e334796c8c89.cab");
        bytes.extend(b"noise aaaa.cab ");
        bytes.extend(b"AABBCCDDEEFF00112233445566778899.cab");
        let names = cabinet_names(&bytes);
        assert_eq!(names, vec!["16ab2ea2187acffa6435e334796c8c89.cab".to_owned(), "aabbccddeeff00112233445566778899.cab".to_owned()]);
    }

    #[test]
    fn vsix_extract_strips_contents_and_rejects_parent_segments() {
        let dir = std::env::temp_dir().join(format!("ascnet-vsix-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&dir).unwrap();
        let archive = dir.join("tool.vsix");
        let mut cursor = Cursor::new(Vec::new());
        {
            let mut writer = zip::ZipWriter::new(&mut cursor);
            let options = zip::write::SimpleFileOptions::default();
            writer.start_file("Contents/VC/Tools/MSVC/14.44.35207/include/vcruntime.h", options).unwrap();
            writer.write_all(b"header").unwrap();
            writer.start_file("manifest.json", options).unwrap();
            writer.write_all(b"{}").unwrap();
            writer.finish().unwrap();
        }
        fs::write(&archive, cursor.get_ref()).unwrap();
        let dest = dir.join("out");
        fs::create_dir_all(&dest).unwrap();
        extract_vsix(&archive, &dest).unwrap();
        assert_eq!(fs::read(dest.join("VC/Tools/MSVC/14.44.35207/include/vcruntime.h")).unwrap(), b"header");
        assert!(!dest.join("manifest.json").exists());

        let mut cursor = Cursor::new(Vec::new());
        {
            let mut writer = zip::ZipWriter::new(&mut cursor);
            let options = zip::write::SimpleFileOptions::default();
            writer.start_file("Contents/../../outside.txt", options).unwrap();
            writer.write_all(b"nope").unwrap();
            writer.finish().unwrap();
        }
        let slipped = dir.join("slip.vsix");
        fs::write(&slipped, cursor.get_ref()).unwrap();
        let error = extract_vsix(&slipped, &dest).unwrap_err().to_string();
        assert!(error.contains("escapes"), "{error}");
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn compiler_vars_point_at_cl_and_the_sdk() {
        let root = std::env::temp_dir().join(format!("ascnet-msvc-{}", uuid::Uuid::new_v4()));
        let tool = root.join("VC/Tools/MSVC/14.44.35207");
        let bin = tool.join("bin/Hostx64/x64");
        fs::create_dir_all(&bin).unwrap();
        fs::create_dir_all(tool.join("include")).unwrap();
        fs::create_dir_all(tool.join("lib/x64")).unwrap();
        fs::write(bin.join("cl.exe"), b"cl").unwrap();
        fs::write(bin.join("link.exe"), b"link").unwrap();
        fs::write(tool.join("include/vcruntime.h"), b"h").unwrap();
        fs::write(tool.join("lib/x64/libcmt.lib"), b"lib").unwrap();
        let include = root.join("Windows Kits/10/Include/10.0.26100.0");
        let lib = root.join("Windows Kits/10/Lib/10.0.26100.0");
        for (dir, name) in [
            (include.join("ucrt"), "corecrt.h"),
            (include.join("um"), "Windows.h"),
            (include.join("shared"), "winapifamily.h"),
            (lib.join("um/x64"), "kernel32.lib"),
            (lib.join("ucrt/x64"), "ucrt.lib"),
        ] {
            fs::create_dir_all(&dir).unwrap();
            fs::write(dir.join(name), b"x").unwrap();
        }
        assert!(!is_ready(&root));
        for name in ["msvcrt.lib", "vcruntime.lib", "oldnames.lib"] {
            fs::write(tool.join("lib/x64").join(name), b"lib").unwrap();
        }
        let vars = compiler_vars(&root).unwrap();
        let value = |key: &str| vars.iter().find(|(name, _)| name == key).map(|(_, value)| value.as_str()).unwrap_or("");
        assert!(value("Path").to_ascii_lowercase().contains("hostx64\\x64") || value("Path").to_ascii_lowercase().contains("hostx64/x64"), "{}", value("Path"));
        assert!(value("INCLUDE").to_ascii_lowercase().contains("ucrt"));
        assert!(value("INCLUDE").to_ascii_lowercase().contains("shared"));
        assert!(value("LIB").to_ascii_lowercase().contains("ucrt"));
        assert!(value("LIB").to_ascii_lowercase().contains("um"));
        assert_eq!(value("VCToolsVersion"), "14.44.35207");
        assert_eq!(value("WindowsSDKVersion"), "10.0.26100.0\\");
        assert!(is_ready(&root));
        fs::remove_file(tool.join("lib/x64/msvcrt.lib")).unwrap();
        assert!(!is_ready(&root));
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    #[ignore]
    fn live_vs_manifest_unpacks_sdk_headers_and_libs() {
        let channel = fs::read_to_string("/tmp/vs-channel.json").expect("channel");
        let manifest = fs::read_to_string("/tmp/VisualStudio.vsman").expect("manifest");
        let info = channel_info(&channel).unwrap();
        assert!(info.manifest_url.starts_with("https://"));
        let selected = plan(&manifest).unwrap();
        eprintln!("toolset {} sdk {}", selected.toolset, selected.sdk_version);
        for item in &selected.vsix {
            eprintln!("vsix {}", item.file_name);
        }
        assert_eq!(selected.toolset, "14.44.17.14");
        assert_eq!(selected.sdk_version, "26100");
        let dest = std::env::temp_dir().join(format!("ascnet-msi-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(&dest).unwrap();
        for msi in SDK_MSIS {
            eprintln!("unpack {msi}");
            extract_msi(Path::new("/tmp/sdk-msi").join(msi).as_path(), Path::new("/tmp/sdk-msi"), &dest).unwrap();
        }
        fn walk(dir: &Path, found: &mut Vec<String>) {
            for entry in fs::read_dir(dir).unwrap().flatten() {
                if entry.path().is_dir() {
                    walk(&entry.path(), found);
                } else {
                    found.push(entry.path().display().to_string());
                }
            }
        }
        let mut found = Vec::new();
        walk(&dest, &mut found);
        eprintln!("{} files", found.len());
        for path in found.iter().take(30) {
            eprintln!("{path}");
        }
        let lower: Vec<String> = found.iter().map(|path| path.to_ascii_lowercase()).collect();
        assert!(lower.iter().any(|path| path.ends_with("windows.h")), "missing windows.h");
        assert!(lower.iter().any(|path| path.ends_with("winapifamily.h")), "missing winapifamily.h");
        assert!(lower.iter().any(|path| path.ends_with("corecrt.h")), "missing corecrt.h");
        assert!(lower.iter().any(|path| path.contains("/um/") && path.contains("/x64/") && path.ends_with("kernel32.lib")), "missing um/x64/kernel32.lib");
        assert!(lower.iter().any(|path| path.contains("/ucrt/") && path.contains("/x64/") && path.ends_with("ucrt.lib")), "missing ucrt/x64/ucrt.lib");
        let _ = fs::remove_dir_all(&dest);
    }
}
