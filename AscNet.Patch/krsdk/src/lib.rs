#[path = "../../diag.rs"]
mod diag;
mod auth;
mod globals;
//mod net;
mod types;
mod ui;
mod util;

mod exports {
    pub mod agreement;
    pub mod config;
    pub mod curl;
    pub mod init;
    pub mod login;
    pub mod memory;
    pub mod report;
    pub mod sdk_identity;
    pub mod unmapped;
}
