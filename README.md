# Ollameter

A live meter for [Ollama](https://ollama.com) on Apple Silicon Macs and Windows PCs: a menu bar or tray gauge and an always-on-top window. It shows live tokens per second, how far along a prompt is, GPU load and power, context and prompt cache use, and the timings of recent requests.

<img src="docs/window.png" alt="The Ollameter window while a model generates" width="400">

## What it shows

- **Menu bar (Mac):** a ring for GPU load, which fills with prompt progress while a long prompt is read, and the live tokens per second.
- **Notification area (Windows):** the same ring. A tray icon has no room for text, so the speed is in its tooltip and the window.
- **Window:** generation and prompt speed, time to first token, cache reuse, context and prompt cache fill, GPU power and temperature, memory and recent requests. The chevron switches to a compact view with just the headline numbers.
- **Unload button:** frees the loaded model's memory without waiting for its keep-alive to run out. It asks for a second click, since reloading a large model and rereading its prompts takes a while, and it's disabled while the model is working.

Left-click the icon to show or hide the window. Right-click it for the menu. The window stays above other windows until you uncheck **Keep Window on Top** there. On Windows it never takes focus from the app you're using, and you can drag it anywhere.

## Watch other computers

The same app can share its computer's numbers and show other computers', Macs and PCs alike, such as a desktop that runs the models while you work on a laptop.

1. On each computer that runs Ollama, right-click the icon and turn on **Share on Local Network**. The menu then shows the name and port others use.
2. On the computer you're watching from, right-click the icon and check the computers you want under **Show Stats From**. Computers found with Bonjour are listed there. For one Bonjour can't see, such as over a VPN, choose **Other Computer…** and enter its name or IP address.

Check as many as you like, including this one. Each gets its own section, headed by its name: the compact window stacks them, and the full window puts them side by side. The icon follows whichever is busiest, and its tooltip has a line for each. Each section describes its own computer's hardware, so a PC shows GPU temperature and free VRAM where a Mac shows its SoC die and swap.

The first time, macOS asks whether Ollameter may find devices on your local network, and Windows asks whether it may use your network. Allow it on private networks; sharing needs that.

Sharing is off until you turn it on. It's read-only: other computers can watch, but can't unload models or change anything. It has no password, so anyone on your network can read the numbers, including model names and prompt sizes but never prompt text.

Scripts can read the same data:

```sh
curl http://studio.local:11480/v1/state    # the latest update as JSON
curl -N http://studio.local:11480/v1/events # every update, as server-sent events
```

## Requirements

- **Mac:** Apple Silicon with macOS 14 or later.
- **PC:** Windows 11, or a recent Windows 10, on x64 or ARM64. Finding other computers uses the Bonjour support built into recent Windows 10 builds and Windows 11; without it, add them by name or address. NVIDIA GPUs report load, memory, power and temperature. Other GPUs report load and memory only.
- Ollama on the same computer at `127.0.0.1:11434`. Only models on Ollama's llama.cpp runner are measured; a model on the MLX runner reads as idle.

## Install

Download the zip for your computer from [Releases](../../releases).

**Mac:** unzip it and move `Ollameter.app` to Applications. The app isn't notarized, so macOS blocks the first launch. Either open it once, then click **Open Anyway** in System Settings → Privacy & Security, or run:

```sh
xattr -dr com.apple.quarantine /Applications/Ollameter.app
```

**Windows:** unzip `Ollameter.exe` anywhere, such as a folder in your user profile, and run it. It needs no installer and no .NET. The exe isn't signed, so SmartScreen may warn the first time: click **More info**, then **Run anyway**. To start it when you sign in, check **Start with Windows** in its menu. Its settings live in `%APPDATA%\Ollameter`.

## Where the numbers come from

Ollameter needs no special permissions.

- **llama-server's `/slots` endpoint** gives per-token counts while a request runs. Ollama starts llama-server on a random port, which Ollameter reads from the process list.
- **Ollama's `server.log`** gives each prompt's size and progress, final timings and the prompt cache's state. It's `~/.ollama/logs/server.log` on a Mac and `%LOCALAPPDATA%\Ollama\server.log` on Windows.
- **Ollama's `/api/ps`** lists loaded models. It's polled sparingly, because every call adds a line to the server log.
- **On a Mac, IOReport and IOHID** give GPU power and the SoC die temperature.
- **On a PC, NVML**, which NVIDIA's driver installs, gives GPU load, memory, power and temperature. Without it, Windows' GPU performance counters give load and memory.

## Build

The Mac app is a Swift package at the top of the repository:

```sh
swift test
scripts/build-app.sh
open build/Ollameter.app
```

The Windows app is a .NET 10 solution in `windows/`. Its core is a port of `Sources/MeterCore`, so a change to one belongs in the other, and its tests match the Swift ones. Both test suites read the frames in `Tests/Fixtures`, so each app is checked against the other's format.

```powershell
dotnet test windows/Ollameter.Core.Tests
dotnet run --project windows/Ollameter
windows/build.ps1 -Version 0.4.0   # a self-contained exe, zipped, in windows/build
```

These flags help when changing the UI, in both apps:

- `--render DIR` writes PNGs of every window state from sample data, in light and dark mode. Add `--live` to include one from this computer.
- `--self-test DIR --seconds N` runs the real app for N seconds, records every frame it publishes, and writes a report with images of the window. On a Mac it also drives the window and menu bar item, and `--unload-test MODEL` clicks the unload button, acting only if MODEL is the only model loaded.
- `--share` turns sharing on for one run, and `--connect HOST[:PORT]` (or `service:NAME` for a Bonjour name, or `local`) shows that computer for one run. Repeat `--connect` to show several. Neither is saved.

## Releases

Push a tag such as `v0.2.0` and the Release workflow tests and builds both apps and publishes them: the Mac app and the Windows exe for x64 and ARM64. A tag with a hyphen, such as `v0.2.0-beta.1`, becomes a prerelease. Running the workflow by hand from the Actions tab does the same builds and attaches the zips to the run without publishing anything.

## License

MIT. See [LICENSE](LICENSE).
