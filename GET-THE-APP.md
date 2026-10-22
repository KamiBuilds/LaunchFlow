# Get LaunchFlow

**Install file:** `LaunchFlow-Setup-v0.7.8-x64.exe`

## Best path (public download)

1. Put this repo on **GitHub**
2. Actions → **Release Windows installer** → Run workflow  
   (or push a tag `v0.7.8`)
3. Open the new **Release** → download the setup `.exe`
4. Optional SmartScreen trust: add repo secrets  
   `LAUNCHFLOW_SIGN_PFX_BASE64` + `LAUNCHFLOW_SIGN_PFX_PASSWORD`  
   (details: [CODE-SIGNING.md](CODE-SIGNING.md))

Then set Settings → Security → update feed to:  
`https://api.github.com/repos/<you>/<repo>/releases/latest`

## Local build (once)

```text
MAKE-APP-INSTALLER.cmd
→ dist\LaunchFlow-Setup-v0.7.8-x64.exe
```
