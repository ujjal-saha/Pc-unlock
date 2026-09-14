# Pc-unlock
A zero-click Windows PC unlocker. Uses a custom C++ Credential Provider, a C# background service, and a Cloudflare relay to securely unlock your PC via Android push notifications. Includes a full setup wizard!
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
2. Download `PhoneUnlock_Setup.exe` `PhoneUnlock.apk`.
3. Run the Setup Wizard and install PhoneUnlock in phone .
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
