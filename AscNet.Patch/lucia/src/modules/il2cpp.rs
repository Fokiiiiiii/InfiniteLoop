//! IL2CPP runtime access shared by the native game-tweak workers (no-fade, FPS unlock): export table,
//! waiting for the domain + attaching the worker thread, class/method lookup, and OK/FAILED logging.
//! Everything goes through the `il2cpp_*` exports of GameAssembly.dll; no offsets, no bundle edits.
use std::{ffi::{c_void, CString}, time::Duration};

use anyhow::{bail, Result};

use crate::diag;
use crate::util::{c_string, get_export};

pub const FLAG_STATIC: u32 = 0x10;
pub const PERIOD: Duration = Duration::from_secs(2);

type DomainGet = unsafe extern "system" fn() -> *mut c_void;
type DomainGetAssemblies = unsafe extern "system" fn(*mut c_void, *mut usize) -> *mut *mut c_void;
type AssemblyGetImage = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ImageGetName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type ClassFromName = unsafe extern "system" fn(*mut c_void, *const i8, *const i8) -> *mut c_void;
type ClassInit = unsafe extern "system" fn(*mut c_void);
type ClassGetField = unsafe extern "system" fn(*mut c_void, *const i8) -> *mut c_void;
type ClassGetMethod = unsafe extern "system" fn(*mut c_void, *const i8, i32) -> *mut c_void;
type FieldFlags = unsafe extern "system" fn(*mut c_void) -> u32;
type FieldType = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type FieldStaticGet = unsafe extern "system" fn(*mut c_void, *mut c_void);
type FieldStaticSet = unsafe extern "system" fn(*mut c_void, *mut c_void);
type MethodFlags = unsafe extern "system" fn(*mut c_void, *mut u32) -> u32;
type MethodParamCount = unsafe extern "system" fn(*mut c_void) -> u32;
type MethodParam = unsafe extern "system" fn(*mut c_void, u32) -> *mut c_void;
type TypeName = unsafe extern "system" fn(*mut c_void) -> *const i8;
type Free = unsafe extern "system" fn(*mut c_void);
type RuntimeInvoke = unsafe extern "system" fn(*mut c_void, *mut c_void, *mut *mut c_void, *mut *mut c_void) -> *mut c_void;
type ObjectUnbox = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
type ThreadCurrent = unsafe extern "system" fn() -> *mut c_void;
type ThreadAttach = unsafe extern "system" fn(*mut c_void) -> *mut c_void;

unsafe fn export<T>(name: &str) -> Result<T> {
    Ok(std::mem::transmute_copy(&get_export(name)?))
}

pub fn ok(what: &str, detail: &str) {
    diag::log(&diag::ok_line("lucia", what, detail));
}

/// `consequence` tells the player what still works, e.g. "the game is unaffected".
pub fn failed(what: &str, reason: &str, consequence: &str) {
    diag::log(&diag::failed_line("lucia", what, &format!("{reason}; {consequence}")));
}

/// Logs the first result and every later change; the steady state stays silent.
pub fn report(last: &mut Option<bool>, good: bool, what: &str, problem: &str, consequence: &str) {
    if *last == Some(good) {
        return;
    }
    *last = Some(good);
    if good {
        ok(what, "re-applied every 2 s");
    } else {
        failed(what, problem, consequence);
    }
}

pub struct Il2cpp {
    domain_get: DomainGet,
    domain_assemblies: DomainGetAssemblies,
    thread_current: ThreadCurrent,
    thread_attach: ThreadAttach,
    class_from_name: ClassFromName,
    image_name: ImageGetName,
    image_of: AssemblyGetImage,
    pub class_init: ClassInit,
    pub class_field: ClassGetField,
    pub class_method: ClassGetMethod,
    pub field_flags: FieldFlags,
    pub field_type: FieldType,
    pub static_get: FieldStaticGet,
    pub static_set: FieldStaticSet,
    pub method_flags: MethodFlags,
    pub param_count: MethodParamCount,
    pub param: MethodParam,
    type_name: TypeName,
    free: Free,
    pub invoke: RuntimeInvoke,
    unbox: ObjectUnbox,
}

impl Il2cpp {
    pub unsafe fn load() -> Result<Self> {
        Ok(Self {
            domain_get: export("il2cpp_domain_get")?,
            domain_assemblies: export("il2cpp_domain_get_assemblies")?,
            thread_current: export("il2cpp_thread_current")?,
            thread_attach: export("il2cpp_thread_attach")?,
            class_from_name: export("il2cpp_class_from_name")?,
            image_name: export("il2cpp_image_get_name")?,
            image_of: export("il2cpp_assembly_get_image")?,
            class_init: export("il2cpp_runtime_class_init")?,
            class_field: export("il2cpp_class_get_field_from_name")?,
            class_method: export("il2cpp_class_get_method_from_name")?,
            field_flags: export("il2cpp_field_get_flags")?,
            field_type: export("il2cpp_field_get_type")?,
            static_get: export("il2cpp_field_static_get_value")?,
            static_set: export("il2cpp_field_static_set_value")?,
            method_flags: export("il2cpp_method_get_flags")?,
            param_count: export("il2cpp_method_get_param_count")?,
            param: export("il2cpp_method_get_param")?,
            type_name: export("il2cpp_type_get_name")?,
            free: export("il2cpp_free")?,
            invoke: export("il2cpp_runtime_invoke")?,
            unbox: export("il2cpp_object_unbox")?,
        })
    }

    /// Blocks until the IL2CPP domain lists assemblies, then attaches the calling thread for the rest
    /// of the process (the worker loops until the game exits). Returns the loaded assemblies.
    pub unsafe fn wait_attach(&self) -> Result<Vec<*mut c_void>> {
        let (domain, assemblies) = loop {
            let domain = (self.domain_get)();
            if !domain.is_null() {
                let mut count = 0;
                let list = (self.domain_assemblies)(domain, &mut count);
                if !list.is_null() && count != 0 {
                    break (domain, std::slice::from_raw_parts(list, count).to_vec());
                }
            }
            std::thread::sleep(Duration::from_millis(100));
        };
        if (self.thread_current)().is_null() && (self.thread_attach)(domain).is_null() {
            bail!("il2cpp_thread_attach failed")
        }
        Ok(assemblies)
    }

    pub unsafe fn type_name_of(&self, type_info: *mut c_void) -> String {
        let pointer = (self.type_name)(type_info);
        let name = c_string(pointer).unwrap_or_else(|| "<invalid>".into());
        if !pointer.is_null() {
            (self.free)(pointer as *mut c_void);
        }
        name
    }

    /// Class in whichever loaded assembly defines it (`namespace` is "" for the global namespace).
    pub unsafe fn find_class(&self, assemblies: &[*mut c_void], namespace: &str, name: &str) -> Option<(*mut c_void, String)> {
        let (namespace, name) = (CString::new(namespace).ok()?, CString::new(name).ok()?);
        assemblies.iter().find_map(|&assembly| {
            let image = (self.image_of)(assembly);
            if image.is_null() {
                return None;
            }
            let class = (self.class_from_name)(image, namespace.as_ptr(), name.as_ptr());
            (!class.is_null()).then(|| (class, c_string((self.image_name)(image)).unwrap_or_else(|| "<unnamed>".into())))
        })
    }

    /// Static method `class_name.name` taking exactly the given parameter types (`System.Int32`, ...).
    pub unsafe fn static_method(&self, class: *mut c_void, class_name: &str, name: &str, params: &[&str]) -> Result<*mut c_void> {
        let c_name = CString::new(name)?;
        let method = (self.class_method)(class, c_name.as_ptr(), params.len() as i32);
        if method.is_null() || (self.param_count)(method) as usize != params.len() {
            bail!("`{class_name}.{name}` with {} parameter(s) not found in this build", params.len())
        }
        if (self.method_flags)(method, std::ptr::null_mut()) & FLAG_STATIC == 0 {
            bail!("`{class_name}.{name}` is not static in this build")
        }
        for (index, expected) in params.iter().enumerate() {
            let got = self.type_name_of((self.param)(method, index as u32));
            if got != *expected {
                bail!("`{class_name}.{name}` parameter {index} has type `{got}`, expected {expected}")
            }
        }
        Ok(method)
    }

    /// Calls a static `void m(int)`; `false` if the call threw a managed exception.
    pub unsafe fn call_set_int(&self, method: *mut c_void, mut value: i32) -> bool {
        let mut args = [(&mut value as *mut i32).cast::<c_void>()];
        let mut exception = std::ptr::null_mut();
        (self.invoke)(method, std::ptr::null_mut(), args.as_mut_ptr(), &mut exception);
        exception.is_null()
    }

    /// Calls a static `int m()`; `None` if the call threw or returned null.
    pub unsafe fn call_get_int(&self, method: *mut c_void) -> Option<i32> {
        let mut exception = std::ptr::null_mut();
        let boxed = (self.invoke)(method, std::ptr::null_mut(), std::ptr::null_mut(), &mut exception);
        (exception.is_null() && !boxed.is_null()).then(|| *((self.unbox)(boxed) as *const i32))
    }
}
