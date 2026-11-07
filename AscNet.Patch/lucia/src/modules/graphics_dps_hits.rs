//! Normal-fight entry capture. Poll totals remain authoritative when this optional hook fails.
use std::{cell::UnsafeCell, ffi::c_void, ptr::null_mut, sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering::{Acquire, Relaxed, Release}}};
use anyhow::{anyhow, bail, Result};
use ilhook::x64::Registers;
use super::graphics_tweaks::{code, QUITTING};
use crate::{interceptor::Interceptor, modules::il2cpp::{self, export, Il2cpp, FLAG_STATIC}};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub(super) struct Hit {
    pub launcher_id: i32,
    pub target_id: i32,
    pub damage_id: i32,
    pub damage: u32,
    /// `IsCrit`, or the `DoubleCrit` custom damage flag (an upgraded crit; counted as a crit).
    pub crit: bool,
}

const CAPACITY: usize = 4096;
struct Slot { sequence: AtomicUsize, hit: UnsafeCell<Hit> }
// A slot is exclusively owned between reserving its sequence and publishing/releasing it.
unsafe impl Sync for Slot {}
struct Ring<const N: usize> {
    slots: [Slot; N],
    write: AtomicUsize,
    read: AtomicUsize,
    dropped: AtomicU64,
}
impl<const N: usize> Ring<N> {
    const fn new() -> Self {
        let mut slots = [const { Slot { sequence: AtomicUsize::new(0), hit: UnsafeCell::new(Hit { launcher_id: 0, target_id: 0, damage_id: 0, damage: 0, crit: false }) } }; N];
        let mut i = 0;
        while i < N { slots[i].sequence = AtomicUsize::new(i); i += 1; }
        Self { slots, write: AtomicUsize::new(0), read: AtomicUsize::new(0), dropped: AtomicU64::new(0) }
    }
    fn push(&self, hit: Hit) {
        let mut position = self.write.load(Relaxed);
        // ponytail: bounded contention drops a hit rather than delaying the fight thread.
        for _ in 0..8 {
            let slot = &self.slots[position % N];
            let sequence = slot.sequence.load(Acquire);
            let difference = sequence.wrapping_sub(position) as isize;
            if difference == 0 {
                match self.write.compare_exchange_weak(position, position.wrapping_add(1), Relaxed, Relaxed) {
                    Ok(_) => {
                        unsafe { *slot.hit.get() = hit; }
                        slot.sequence.store(position.wrapping_add(1), Release);
                        return;
                    }
                    Err(next) => position = next,
                }
            } else if difference < 0 { break; }
            else { position = self.write.load(Relaxed); }
        }
        self.dropped.fetch_add(1, Relaxed);
    }
    // Exactly one consumer: the graphics worker.
    fn pop(&self) -> Option<Hit> {
        let position = self.read.load(Relaxed);
        let slot = &self.slots[position % N];
        if slot.sequence.load(Acquire) != position.wrapping_add(1) { return None; }
        let hit = unsafe { *slot.hit.get() };
        slot.sequence.store(position.wrapping_add(N), Release);
        self.read.store(position.wrapping_add(1), Relaxed);
        Some(hit)
    }
}
static RING: Ring<CAPACITY> = Ring::new();
static ENABLED: AtomicBool = AtomicBool::new(false);
static MASTER: AtomicUsize = AtomicUsize::new(0);
pub(super) fn drain() -> impl Iterator<Item = Hit> {
    std::iter::from_fn(|| RING.pop()).take(CAPACITY)
}
pub(super) fn dropped() -> u64 { RING.dropped.load(Relaxed) }

type Master = unsafe extern "win64" fn(usize, *mut c_void) -> usize;
unsafe extern "win64" fn on_damage(registers: *mut Registers, _: usize) {
    if QUITTING.load(Relaxed) || !ENABLED.load(Acquire) { return; }
    let registers = &*registers;
    let mut launcher = registers.rdx as usize;
    let target = registers.r8 as usize;
    let damage = registers.r9 as i32;
    if launcher == 0 || target == 0 || damage <= 0 { return; }
    let getter = MASTER.load(Relaxed);
    if getter == 0 { return; }
    let master: Master = std::mem::transmute(getter);
    let mut seen = [0usize; 8];
    for index in 0..8 {
        seen[index] = launcher;
        let next = master(launcher, null_mut());
        if next == 0 { break; }
        // A cyclic or deeper-than-supported owner chain cannot be attributed reliably.
        if seen[..=index].contains(&next) || index == 7 { return; }
        launcher = next;
    }
    // ilhook 2.3.0 write_trampoline_prolog first executes push rsp, then preserves that
    // value in Registers.rsp before relocating ANY original instructions. This is entry RSP.
    let context = *((registers.rsp as usize + 0x48) as *const usize);
    let crit = context != 0 && (*((context + 0x4C) as *const u8) != 0 || *((context + 0x50) as *const i32) == 1);
    RING.push(Hit {
        launcher_id: *((launcher + 0x2C) as *const i32),
        target_id: *((target + 0x2C) as *const i32),
        damage_id: if context == 0 { 0 } else { *((context + 0x20) as *const i32) },
        damage: damage as u32, crit,
    });
}

#[derive(Default)]
pub(super) struct DamageHook { hook: Option<Interceptor>, attempted: bool }
impl DamageHook {
    pub(super) unsafe fn update(&mut self, il: &Il2cpp, assemblies: &[*mut c_void], enabled: bool) {
        ENABLED.store(enabled && self.hook.is_some(), Release);
        if !enabled || self.attempted || QUITTING.load(Relaxed) { return; }
        self.attempted = true;
        match Self::install(il, assemblies) {
            Ok(hook) => { self.hook = Some(hook); ENABLED.store(true, Release); }
            Err(error) => il2cpp::failed("DPS hit hook", &format!("{error:#}"), "hit statistics unavailable; polled damage totals still shown"),
        }
    }
    unsafe fn install(il: &Il2cpp, assemblies: &[*mut c_void]) -> Result<Interceptor> {
        let (result, _) = il.find_class(assemblies, "", "XFightResult").ok_or_else(|| anyhow!("XFightResult missing"))?;
        let method = (il.class_method)(result, c"OnDamage".as_ptr(), 9);
        if method.is_null() || (il.param_count)(method) != 9 || (il.method_flags)(method, null_mut()) & FLAG_STATIC != 0 { bail!("XFightResult.OnDamage is not a nine-argument instance method"); }
        for (index, expected) in ["XNpc", "XNpc", "System.Int32", "System.Int32", "System.Int32", "System.Int32", "System.Int32", "XDamageMagicContext", "System.Int32"].iter().enumerate() {
            let actual = il.type_name_of((il.param)(method, index as u32));
            if actual != *expected { bail!("OnDamage parameter {index}: expected {expected}, got {actual}"); }
        }
        type ReturnType = unsafe extern "system" fn(*mut c_void) -> *mut c_void;
        type FieldOffset = unsafe extern "system" fn(*mut c_void) -> usize;
        let return_type: ReturnType = export("il2cpp_method_get_return_type")?;
        let field_offset: FieldOffset = export("il2cpp_field_get_offset")?;
        if il.type_name_of(return_type(method)) != "System.Void" { bail!("OnDamage return type is not System.Void"); }
        let (npc, _) = il.find_class(assemblies, "", "XNpc").ok_or_else(|| anyhow!("XNpc missing"))?;
        let (context, _) = il.find_class(assemblies, "", "XDamageMagicContext").ok_or_else(|| anyhow!("XDamageMagicContext missing"))?;
        for (class, name, expected_type, offset) in [(npc, c"Id", "System.Int32", 0x2C), (context, c"Id", "System.Int32", 0x20), (context, c"IsCrit", "System.Boolean", 0x4C), (context, c"DamageCustomEnum", "XDamageCustomEnum", 0x50)] {
            let field = (il.class_field)(class, name.as_ptr());
            if field.is_null() || (il.field_flags)(field) & FLAG_STATIC != 0 || field_offset(field) != offset || il.type_name_of((il.field_type)(field)) != expected_type { bail!("DPS field layout mismatch: {name:?}"); }
        }
        let getter = (il.class_method)(npc, c"get_Master".as_ptr(), 0);
        if getter.is_null() || (il.param_count)(getter) != 0 || (il.method_flags)(getter, null_mut()) & FLAG_STATIC != 0 || il.type_name_of(return_type(getter)) != "XNpc" { bail!("XNpc.get_Master signature mismatch"); }
        // 4.8 RVA 855A70 is not a backing-field getter: it probes ILFix patch 0x199,
        // then indexes npc+0x88's component array (length >7, element +0x58).
        // Call the native getter, never runtime_invoke; this preserves patched ownership.
        MASTER.store(code(getter, "XNpc.get_Master")?, Relaxed);
        let address = code(method, "XFightResult.OnDamage")?;
        let mut hook = Interceptor::new();
        hook.attach(address, on_damage)?;
        il2cpp::ok("DPS hit hook", &format!("XFightResult.OnDamage entry at 0x{address:X}; bounded native capture enabled"));
        Ok(hook)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ring_overflow_fifo_and_reuse() {
        let ring = Ring::<2>::new();
        let hit = |damage| Hit { damage, ..Hit::default() };
        ring.push(hit(1)); ring.push(hit(2)); ring.push(hit(3));
        assert_eq!(ring.dropped.load(Relaxed), 1);
        assert_eq!(ring.pop(), Some(hit(1)));
        ring.push(hit(4));
        assert_eq!(ring.pop(), Some(hit(2)));
        assert_eq!(ring.pop(), Some(hit(4)));
        assert_eq!(ring.pop(), None);
    }
    #[test]
    fn ring_concurrent_producers_publish_complete_records() {
        let ring = Ring::<4096>::new();
        std::thread::scope(|scope| {
            for producer in 0..4 {
                let ring = &ring;
                scope.spawn(move || for damage in 1..=128 { ring.push(Hit { launcher_id: producer, damage, target_id: damage as i32, ..Hit::default() }); });
            }
        });
        let mut count = 0;
        while let Some(hit) = ring.pop() { assert_eq!(hit.damage as i32, hit.target_id); count += 1; }
        assert_eq!(count + ring.dropped.load(Relaxed), 512);
    }
}
