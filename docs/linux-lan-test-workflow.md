# Linux LAN Test Workflow

This workflow installs Recconect from the Windows repo checkout into the R.E.P.O. Steam install on the LAN Linux machine:

- SSH target: `yoomarxd@192.168.200.3`
- Detected game path: `/home/yoomarxd/snap/steam/common/.local/share/Steam/steamapps/common/REPO`
- Steam runtime: Linux Steam snap running the Windows game through Proton
- Loader installed by this workflow: bundled `BepInEx_win_x64_5.4.23.5.zip`

## Install Or Update

From this repo on Windows:

```powershell
.\scripts\install-recconect-remote-linux.ps1 -ConfigMode Experimental -Force
```

To skip rebuilding and deploy the current local `bin/Debug/netstandard2.1/Recconect.dll`:

```powershell
.\scripts\install-recconect-remote-linux.ps1 -NoBuild -ConfigMode Experimental -Force
```

## List Remote Installs

```powershell
.\scripts\install-recconect-remote-linux.ps1 -ListGameInstalls
```

If auto-detection ever finds multiple installs, pass the exact game path:

```powershell
.\scripts\install-recconect-remote-linux.ps1 `
  -GamePath "/home/yoomarxd/snap/steam/common/.local/share/Steam/steamapps/common/REPO" `
  -ConfigMode Experimental `
  -Force
```

## Fetch Remote Log

After launching R.E.P.O. on the Linux machine and reproducing a reconnect case:

```powershell
.\scripts\install-recconect-remote-linux.ps1 -FetchLog
```

The log is copied into `artifacts/linux-LogOutput-<timestamp>.log`.

Remote log path:

```text
/home/yoomarxd/snap/steam/common/.local/share/Steam/steamapps/common/REPO/BepInEx/LogOutput.log
```

## Notes

- The Linux install is a Proton game folder, so the Windows x64 BepInEx pack is expected: `winhttp.dll` and `doorstop_config.ini` live next to `REPO.exe`.
- The installer writes `BepInEx/plugins/YoomarXD-Recconect/Recconect.dll`.
- The installer writes `BepInEx/config/com.yoomarxd.recconect.cfg`.
- Current test mode should stay `Experimental` so the deep reconnect diagnostics are active.
