# Wiki screenshots

The images in `docs/wiki/images` are taken from a local build by driving a headless
Microsoft Edge. To retake them after a UI change:

1. Start the app locally on port 5210 (`dotnet run --project src/LabelMaker --urls http://localhost:5210`).
2. From the repository root, in PowerShell:

   ```powershell
   $out = "$env:TEMP\wlm-shots"; New-Item -ItemType Directory -Force $out | Out-Null
   & docs\wiki-tools\capture.ps1 -OutDir $out -Steps (Resolve-Path docs\wiki-tools\steps.ps1)
   Copy-Item "$out\*.png" docs\wiki\images -Force
   ```

3. Publish with `bash docs/wiki/publish.sh`.

`capture.ps1` is a small DevTools-protocol driver (evaluate JavaScript, save a screenshot
of chosen elements). `steps.ps1` is the sequence of screens; add to it when the
documentation gains a new picture. Not every image it saves is used by the wiki.
