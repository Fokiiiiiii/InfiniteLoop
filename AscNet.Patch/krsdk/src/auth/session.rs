use crate::types::UserSession;
use serde::{Deserialize, Serialize};
use std::path::PathBuf;
use windows::core::PCWSTR;
use windows::Win32::Foundation::{LocalFree, HLOCAL};
use windows::Win32::Security::Cryptography::{
    CryptProtectData, CryptUnprotectData, CRYPTPROTECT_UI_FORBIDDEN, CRYPT_INTEGER_BLOB,
};

/// Last successful KRSDK login for one server. The password is never written here.
///
/// The account token is the server's GUID and has no expiry. Login sends it only
/// to the origin stored beside it, then deletes this file when that server rejects
/// it. A client-side timer would drop a token the server still accepts.
#[derive(Serialize, Deserialize)]
struct StoredSession {
    origin: String,
    username: String,
    uid: String,
    #[serde(rename = "protectedToken")]
    protected_token: String,
}

fn path() -> Option<PathBuf> {
    let base = std::env::var_os("LOCALAPPDATA")?;
    Some(PathBuf::from(base).join("AscNet").join("krsdk-session.json"))
}

pub fn load(origin: &str) -> Option<UserSession> {
    let path = path()?;
    let text = std::fs::read_to_string(&path).ok()?;
    let stored: StoredSession = match serde_json::from_str(&text) {
        Ok(stored) => stored,
        Err(_) => {
            // Older files kept a plaintext token and named no server. Drop them.
            let _ = std::fs::remove_file(&path);
            return None;
        }
    };
    if stored.origin != origin
        || stored.username.is_empty()
        || stored.uid.is_empty()
        || stored.protected_token.is_empty()
    {
        return None;
    }
    let token = unprotect(origin, &stored.protected_token)?;
    if token.is_empty() {
        return None;
    }
    Some(UserSession {
        username: stored.username,
        token,
        uid: stored.uid,
    })
}

pub fn save(origin: &str, session: &UserSession) {
    let Some(path) = path() else {
        return;
    };
    let Some(protected_token) = protect(origin, &session.token) else {
        println!("[KRSDK] saved session could not be protected");
        return;
    };
    if let Some(parent) = path.parent() {
        let _ = std::fs::create_dir_all(parent);
    }
    let stored = StoredSession {
        origin: origin.to_string(),
        username: session.username.clone(),
        uid: session.uid.clone(),
        protected_token,
    };
    if let Ok(text) = serde_json::to_string(&stored) {
        let _ = std::fs::write(path, text);
    }
}

pub fn clear() {
    if let Some(path) = path() {
        let _ = std::fs::remove_file(path);
    }
}

fn protect(origin: &str, token: &str) -> Option<String> {
    let bytes = crypt(origin, token.as_bytes(), true)?;
    Some(hex_encode(&bytes))
}

fn unprotect(origin: &str, protected_token: &str) -> Option<String> {
    let bytes = hex_decode(protected_token)?;
    let plain = crypt(origin, &bytes, false)?;
    String::from_utf8(plain).ok()
}

fn crypt(origin: &str, input: &[u8], protect_data: bool) -> Option<Vec<u8>> {
    if origin.is_empty() || input.is_empty() || input.len() > u32::MAX as usize {
        return None;
    }
    let mut data = input.to_vec();
    let mut entropy = origin.as_bytes().to_vec();
    let data_blob = CRYPT_INTEGER_BLOB {
        cbData: data.len() as u32,
        pbData: data.as_mut_ptr(),
    };
    let entropy_blob = CRYPT_INTEGER_BLOB {
        cbData: entropy.len() as u32,
        pbData: entropy.as_mut_ptr(),
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let succeeded = unsafe {
        if protect_data {
            CryptProtectData(
                &data_blob,
                PCWSTR::null(),
                Some(&entropy_blob),
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
            .is_ok()
        } else {
            CryptUnprotectData(
                &data_blob,
                None,
                Some(&entropy_blob),
                None,
                None,
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
            .is_ok()
        }
    };
    if !succeeded || output.pbData.is_null() || output.cbData == 0 {
        return None;
    }
    let plain = unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        let _ = LocalFree(HLOCAL(output.pbData.cast()));
    }
    Some(plain)
}

fn hex_encode(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for byte in bytes {
        out.push(HEX[(byte >> 4) as usize] as char);
        out.push(HEX[(byte & 0xf) as usize] as char);
    }
    out
}

fn hex_decode(text: &str) -> Option<Vec<u8>> {
    if text.len() % 2 != 0 {
        return None;
    }
    let mut out = Vec::with_capacity(text.len() / 2);
    let bytes = text.as_bytes();
    let mut index = 0;
    while index < bytes.len() {
        let high = hex_value(bytes[index])?;
        let low = hex_value(bytes[index + 1])?;
        out.push((high << 4) | low);
        index += 2;
    }
    Some(out)
}

fn hex_value(byte: u8) -> Option<u8> {
    match byte {
        b'0'..=b'9' => Some(byte - b'0'),
        b'a'..=b'f' => Some(byte - b'a' + 10),
        _ => None,
    }
}
