# Ujjal saha 
# PC Unlocker

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%20%2B%20Android-0078D7?style=for-the-badge&logo=microsoft" alt="Windows + Android" />
  <img src="https://img.shields.io/badge/Language-C%2B%2B%20%2B%20C%23%20%2B%20Kotlin-000000?style=for-the-badge" alt="Languages" />
  <img src="https://img.shields.io/badge/Cloudflare-Relay-F38020?style=for-the-badge" alt="Cloudflare Relay" />
  <img src="https://img.shields.io/badge/Status-Open%20Source-28A745?style=for-the-badge" alt="Open Source" />
</p>

> Zero-click unlock for Windows PCs using Android push notifications, a custom Credential Provider, a Windows background service, and a Cloudflare relay.

## Overview

PC Unlocker is an open-source project that brings a secure, phone-to-PC unlock workflow to Windows systems. Instead of typing a password or PIN on the lock screen, the user can approve an unlock request from their Android phone. The request is sent over a secure relay, wakes the device if needed, and finishes through a custom Windows Credential Provider integrated directly into the login experience.

This project combines:

- a custom C++ Windows Credential Provider
- a C# background service running on the PC
- a Cloudflare relay for routing requests and handling delivery
- Android push notifications to wake the phone when needed

The result is a modern "zero-click" unlock experience built around a real Windows lock screen integration.

---

## What it does & How it works

PC Unlocker is designed to let a trusted Android device approve an unlock request for the local Windows PC without needing to manually type credentials at the machine.

### Core workflow

1. The Windows PC runs a lightweight background service that maintains connection state, handles pairing, and listens for unlock requests.
2. A custom C++ Credential Provider is installed into the Windows logon environment and integrates directly with the lock screen.
3. When the user wants to unlock the PC from their phone, the Android app sends the request through a secure relay layer.
4. The relay forwards the request to the target Windows machine.
5. The Windows service receives the request and passes it into the Credential Provider.
6. The user approves the request from their Android device, and the PC completes the unlock process.

### Why this design

This architecture was chosen because a plain socket-based approach is unreliable while the PC is asleep or in a restricted background state. A Cloudflare relay plus Android push notification wake-up helps ensure the unlock flow still works when the phone is not actively connected to the PC.

### High-level architecture

| Component | Role | Responsibility |
| --- | --- | --- |
| C++ Credential Provider | Lock screen integration | Handles Windows sign-in flow and unlock approval inside the login system |
| C# Windows Service | Background daemon | Maintains identity, pairing, relay connections, and local command flow |
| Cloudflare Relay | Message routing | Connects PC and Android devices, proxies unlock requests, and handles wake-up delivery |
| Android App | Approval surface | Sends unlock requests and approves or rejects the PC unlock action |

---

## Features

- ✅ Zero-click unlock flow from Android to Windows
- ✅ Secure relay-based request passing
- ✅ Windows lock screen integration via a custom Credential Provider
- ✅ Background service registration and persistent PC identity handling
- ✅ Android push notification wake-up support for background delivery
- ✅ Open-source Windows + relay codebase for experimentation and extension

---

## Installation & Usage

For most users, the installation experience is intentionally simple.

### Regular user setup

1. Go to the GitHub Releases tab.
2. Download `PhoneUnlock_Setup.exe`.
3. Run the Setup Wizard.
4. Grant the required UAC elevation when prompted.
5. Let the installer complete its setup steps.
6. The wizard will automatically:
   - elevate as needed
   - register the Windows background service
   - install the Credential Provider into the correct system location
   - configure the PC for pairing and unlock workflows

### Typical flow after installation

1. Pair your Android device with the PC using the setup flow.
2. Ensure the Windows service is running.
3. Trigger the unlock request from the Android app.
4. Approve the request from the phone.
5. The PC unlock process completes through the Credential Provider integration.

> The goal is to keep the end-user experience simple, safe, and focused on one thing: unlocking the machine from your phone without friction.

---

## Developer Journey / Challenges Faced

This project was not built in a straight line. It required a lot of troubleshooting, platform-specific work, and careful Windows integration.

### Key challenges

- **UAC elevation issues**: Getting the installer and service registration flow to work correctly without breaking Windows security boundaries required careful handling of admin rights and service setup.
- **Architecture matching**: The custom C++ provider had to be compiled for the correct OS architecture, which meant careful validation for x64 compatibility and clean configuration of the build target.
- **COM registration and CLSID correctness**: The Credential Provider relies on registry registration and COM identity. A wrong CLSID or bad registration path could break the provider at runtime, so the integration had to be precise.
- **GitHub upload limits**: Large C# build caches and generated artifacts created a practical challenge when preparing the project for public hosting. This required cleaning and organizing the repository to avoid hitting the 100-file upload limit and keep the source clean and shareable.

### Lessons learned

This project is a strong example of how platform integration work often becomes a mix of:

- desktop engineering
- security constraints
- identity management
- relay/network reliability
- Android background delivery concerns

The final result is a working proof-of-concept and a realistic unlock pipeline that demonstrates how a locked Windows PC can be approved securely from a phone.

---

## Project Structure

This repository includes the key components needed for the Windows and relay side of the project:

- `PhoneUnlockService` — the C# background daemon
- `PhoneUnlockSetup` — the user-facing setup and pairing tool
- `PhoneUnlockRelay` — the Cloudflare relay and worker implementation
- `OfficialMicrosoftProvider` — the custom C++ Credential Provider based on the Microsoft sample code



---

## Troubleshooting / Common Problems

Below are the most common issues encountered while developing and testing this project. These problems are practical, real-world problems that can appear during installation, pairing, or lock-screen integration.

### 1. Windows service fails to start or register

**Symptoms**
- Service is not running after install
- Start-up fails with access or dependency errors
- Setup wizard completes but the service is not active

**What to check**
- Run the installer as Administrator
- Confirm the service is installed correctly and registered in Windows Services
- Check that the user running the service has permission to access the required files and registry keys
- Verify that the service is not already running twice

**Typical fix**
- Re-run the setup wizard with UAC elevation enabled
- Restart the machine if the service registration is stale
- Remove duplicate service instances before re-registering

### 2. Credential Provider does not load or shows no effect on the lock screen

**Symptoms**
- Provider is present in the build output, but does not appear on the lock screen
- No unlock UI is displayed after login is requested
- The system does not recognize the provider registration

**What to check**
- Confirm the provider is registered under the correct COM GUID in the registry
- Verify the DLL is in the correct Windows system folder
- Ensure the provider was built for the correct architecture (`x64`)
- Confirm there are no broken registry entries from an earlier install

**Typical fix**
- Re-register the provider cleanly
- Remove stale registry entries before reinstalling
- Rebuild the DLL in `Release` + `x64` mode to match the target OS

### 3. Lock screen integration fails after a rebuild

**Symptoms**
- The provider builds successfully, but lock-screen behavior is inconsistent
- The provider seems to compile but the login UI does not respond correctly
- The provider works in one environment but not another

**What to check**
- Confirm the provider binary matches the system architecture
- Check whether the COM registration was generated for the correct build config
- Validate that there are no stale `dll` or registry leftovers from earlier test runs
- Make sure you are not mixing debug and release artifacts during testing

**Typical fix**
- Build from a clean state
- Remove old registration entries
- Copy the correct release binary into the system folder
- Reboot and retest

### 4. Relay authentication mismatch / pairing fails with 401 responses

**Symptoms**
- Pairing fails immediately
- WebSocket or relay connection returns a `401 Unauthorized`
- The configured PC identity does not match the token or persisted pairing record

**What to check**
- Ensure the same canonical PC ID is used consistently across the service, pairing store, relay registration, and WebSocket connection
- Check the relay token registration against the currently persisted PC identity
- Look for stale values from an earlier debug environment or a previous test VM

**Typical fix**
- Remove stale or mismatched pairing data
- Re-register the PC identity in the relay
- Ensure there is only one active service instance using the same identity

### 5. Named pipe communication breaks or hangs

**Symptoms**
- The service and provider cannot communicate
- Unlock requests stall or stop mid-flight
- The provider appears to send a message but nothing arrives

**What to check**
- Verify that the named pipe path is correct
- Confirm there is no duplicate service instance competing for the same pipe
- Check for ACL issues and broken pipe errors
- Review Windows event logs and local logs for communication failures

**Typical fix**
- Restart the service
- Ensure only one service instance is active
- Recreate the named pipe setup correctly
- Make sure both sides are using the same pipe identifier and message contract

### 6. Android app does not receive notifications reliably

**Symptoms**
- Unlock requests are delayed until the app is manually opened
- Push delivery is low-priority or not waking the app in time
- The phone appears offline even when the relay is active

**What to check**
- Confirm FCM payload priority is set correctly
- Ensure the app is not blocked by battery optimization
- Verify that Android notification permissions are granted
- Check whether the app is being background-killed by the OEM battery policy

**Typical fix**
- Request the battery optimization exemption from the app
- Use a high-priority FCM payload for wake-up behavior
- Keep the relay and Android flow validated under actual device conditions

### 7. Project build fails because of stale generated files or duplicate outputs

**Symptoms**
- Build output appears inconsistent
- Source compiles but previous binary artifacts keep interfering
- Debug and release outputs look mismatched

**What to check**
- Clear stale build artifacts and intermediate objects
- Verify you are not overwriting the active build output while the service is still running
- Check whether multiple processes are started against the same target build directory

**Typical fix**
- Delete old `bin` and `obj` artifacts before a fresh rebuild
- Stop duplicate service processes before re-testing
- Rebuild from a clean configuration

### 8. App settings or pairing data are stale after a repeat install

**Symptoms**
- The app behaves as though it is using an old PC identity
- The UI shows a previous pairing state or outdated connection details
- Pairing fails even after reinstalling components

**What to check**
- Remove stale persisted pairing files and reconnect the PC
- Check for multiple config locations or older registry states
- Confirm a single canonical PC ID is being used for the pairing lifecycle

**Typical fix**
- Remove old stored identity data
- Recreate the pairing flow from a clean state
- Ensure the current setup is using the correct generated ID

### 9. GitHub upload or repo packaging issues

**Symptoms**
- Upload fails during repository preparation
- Large binary caches or generated directories consume the project size budget
- The project cannot be published cleanly

**What to check**
- Remove large build caches and temporary outputs
- Keep source and debug artifacts only where needed
- Avoid committing full dependency folders or generated logs

**Typical fix**
- Keep only the essential project folders and required debug binaries
- Exclude `node_modules`, cache folders, and temporary runtime logs
- Use a clean, curated open-source structure

### 10. Setup UI reports `ReadMode is not of PipeTransmissionMode.Message`

**Cause**
- The Setup UI calls `IsMessageComplete` while the named-pipe client is still using byte read mode.

**Fix**
- Set `pipe.ReadMode = PipeTransmissionMode.Message` immediately after `ConnectAsync`.
- Rebuild `PhoneUnlockSetup` in `Release` mode and replace the installed Setup executable and DLLs.

### 11. Pairing code remains unavailable even though the service is running

**What to check**
- Confirm the service exposes `PhoneUnlockPairingPipe`, not only the Credential Provider's `PhoneUnlockPipe`.
- Confirm the installed Setup binary and the service are from the same Release build.
- Check whether the service can connect to the relay; a running Windows service does not guarantee an active relay connection.

**Typical fix**
- Restart or reinstall `PhoneUnlockService` after replacing its binaries.
- Test the pairing pipe with the exact `BEGIN_PAIRING` command.
- Return an explicit `PAIRING_ERROR` response from the service instead of leaving the Setup client waiting for a timeout.

### 12. Setup and service binaries are from different builds

**Symptoms**
- The UI still shows an old generic error after the source was fixed.
- The Setup client expects a pairing pipe or response format that the installed service does not implement.

**Typical fix**
- Rebuild both projects in `Release` mode.
- Copy the complete Setup output to the installer Setup folder and the complete Service output to the installer Service folder.
- Remove stale files before copying and restart the installed service as Administrator.
- Verify the staged files with SHA-256 hashes before building the installer.

### 13. Windows refuses to restart the service during testing

**Cause**
- Stopping or replacing a Windows Service normally requires administrator privileges, and a running service may keep old binaries loaded.

**Typical fix**
- Run Services, PowerShell, or the installer elevated as Administrator.
- Restart `PhoneUnlockService` after installing the updated Service files.
- Close any running `PhoneUnlockSetup.exe` instance before launching the newly staged copy.

### 14. Pairing pipe works locally but the phone does not complete pairing

**What to check**
- Confirm the displayed six-digit code is still within its five-minute validity window.
- Confirm the Android app uses the same relay URL and pairing code.
- Verify the service is connected to the relay before opening the pairing window.
- Remove stale pairing data only when testing a clean pairing flow; do not delete the DPAPI credential store casually.

**Typical fix**
- Generate a fresh code and complete pairing before the window expires.
- Check service logs for relay authentication errors or `401 Unauthorized` responses.
- Ensure only one active service instance is using the PC identity.

---

## Credits

This project builds directly on Microsoft's official Windows Credential Provider sample code. The project heavily adapted and modified that reference implementation to create the custom `SampleHardwareEventCredentialProvider.dll` used to integrate the unlock flow into the Windows lock screen.

We want to explicitly acknowledge the importance of that sample in enabling this project to function as a real Windows authentication experience. Without the official Microsoft reference implementation, this project would not have been able to reach the same level of lock-screen integration.

---

## License

This project is released as open source for educational, experimental, and real-world use. Please review the repository license before commercial deployment or redistribution.

---

## Contributing

Contributions are welcome.

If you want to help improve the project, consider:

- improving the relay reliability and security model
- refining the Android notification experience
- hardening the Windows service and credential-provider flow
- improving the setup and install experience
- documenting edge cases and deployment notes

---

## Status

This project is a working, experimental Windows + Android unlock platform focused on secure local unlock workflows and practical lock-screen integration.

It is best understood as a proof-of-concept and a research-style implementation for secure and modern phone-to-PC access.

---

## Final Note

PC Unlocker was built to explore what a secure, user-friendly, zero-click unlock experience could look like when the lock screen, Windows service, and phone are connected through a real relay system. It is a blend of Windows internals, secure messaging, and mobile-device wakeup logic — and it is designed to be both usable and extensible.

If you're curious, please explore the code, test the setup flow, and help push the project forward.
