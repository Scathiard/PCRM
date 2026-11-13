# PCRM - Plain Craft Resources Manager

[简体中文](README.md)

A Windows desktop resource manager for Minecraft players and modpack creators. Keep shaders, resource packs, YSM models, TaCZ gun packs and other resources in one library, share files across instances, and retain each instance's chosen versions.

## Use cases

- Reuse models, gun packs and shaders across modpacks with less copying and duplicate storage.
- Deploy personal resources from the library to a new instance after a modpack update.
- Keep multiple resource versions and choose upgrades or rollbacks per instance.

## Features

- **Central library:** Import files or complete resource folders without changing external source files; retain different versions with the same name separately.
- **Sharing and deduplication:** Use NTFS hard links on the same volume; copy resources on demand into a cache on another volume for instances there to share.
- **Existing-instance intake:** Scan instances, compare content, and connect duplicate files to the library after confirmation.
- **Version management:** Browse history, confirm version switches, and detach resources into independent copies.
- **Custom types:** Define resource types by relative instance paths and configure ignore patterns.
- **Configuration updates:** Update enabled shader and resource-pack references, inherit shader settings, and report configuration update failures.
- **Status checks:** Distinguish shared latest versions, shared older versions, independent copies, modified content and untracked resources.

## Launcher integration

PCRM manages instance resource directories independently of launchers such as PCL and HMCL. It does not launch Minecraft, manage mod `.jar` files, resolve mod dependencies or download online resources.

Shared files contain the same physical data. Editing a shared file in place outside the manager affects instances using it; detecting a change does not recover overwritten content.

## License

The source is available under [PolyForm Noncommercial 1.0.0](LICENSE), permitting noncommercial use, modification and redistribution as defined by the license. Commercial use is not licensed. This project adds no author-attribution requirement, but redistribution must include the license text or its URL.

Imported Minecraft resources remain subject to their own licenses.
