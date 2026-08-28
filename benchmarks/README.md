# mpv embedding performance benchmark

Use the same media, window size, hardware-decoding option, warm-up period, and
20-second sample for both modes.

```powershell
./bin/Release/net10.0/win-x64/AstraCat.exe --mpv-render-benchmark <video> artifacts/render-api.json
./bin/Release/net10.0/win-x64/AstraCat.exe --mpv-wid-benchmark <video> artifacts/wid.json
```

Both modes run in the same AstraCat process and load the same pinned
`libmpv-2.dll`. WID uses mpv's native Windows VO; Render API uses the Avalonia
OpenGL surface. This avoids comparing against the older bundled `mpv.exe`.

The WID harness also verifies that `time-pos` advances during the sample. The
Render harness records decoder/VO drops, hardware decoder, render-call time,
and average/P95/max absolute A/V sync error. Run benchmarks sequentially; two
simultaneous GUI processes invalidate the CPU comparison.
