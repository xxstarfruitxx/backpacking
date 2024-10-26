# Troubleshooting Issues in SwarmUI

First of all: when in doubt, restart. The tried and true classic of solving IT problems is as true in SwarmUI as anywhere else - when you restart, a lot of problems solve themselves.

Second, feel free to open [issues here on GitHub](https://github.com/mcmonkeyprojects/SwarmUI/issues) or ask on [the SwarmUI Discord](https://discord.gg/q2y38cqjNw) where we have a `#help-forum` available for questions. These are also both great places to search for if anyone else has had the same problem before and already gotten an answer.

## Install Issues

### NuGet Errors

If you see an error message mentioning a failed connection to `https://api.nuget.org/v3/index.json`, such as `Unable to load the service index for source https://api.nuget.org/v3/index.json`

That is the build dependency service, connections to it are required for the first launch. To fix this:
- **A:** Ensure your network is enabled, not blocked by firewalls, VPNs, whatever. If you have anything like that running, disable it and try again. (Usually these aren't a problem but sometimes they can do weird things to your network)
- **B:** Sometimes temporary internet outages are at fault. So just waiting a few hours or until tomorrow and trying again might be all you need.
- **C:** It might also help to restart your computer.
- **D:** You can also try emptying your NuGet cache, see below

If you see errors like: `The type or namespace name 'Hardware' could not be found (are you missing a using directive or an assembly reference?) [src/SwarmUI.csproj]`, noting the mention of `Hardware` specifically (this is a NuGet package), that might mean your NuGet cache is corrupt. This is rare, but can happen. In that case, you must reset your NuGet cache.

#### Reset NuGet Cache

If on Windows, open File Explorer to `%appdata%/NuGet` and delete the entire folder, and then also `%localappdata%/NuGet` and delete that as well.

If on Linux/Mac, `rm -rf ~/.nuget`

## AMD On Windows

If you have an AMD (Radeon) GPU on Windows, by default SwarmUI installs the comfy backend with DirectML. DirectML is a Microsoft Windows library for AI processing that is compatible with AMD, but very buggy and limited compared to the CUDA library used for Nvidia GPUs. A variety of issues exist with this.

If you see an error about `libtorchaudio.pyd`, or `OSError: [WinError 127] The specified procedure could not be found` in relation to `torchaudio`, this is a known bug with AMD+DirectML+Windows, you can ignore it, or swap backend (see below).

If you see `Cannot handle this data type: (1, 1, 3), <f4`, that is because live previews don't work right on AMD+DirectML+Windows, you can go to `Server`->`Backends`->edit the comfy backend->uncheck `EnablePreviews`->`Save` and it should work, or swap backend (see below).

If you see `The parameter is incorrect.`, you are running something incompatible with AMD+DirectML+Windows, and can either just not run that, or swap backend (see below).

### Swap AMD Backend

There are several alternate AMD backend options that work better than DirectML:

- **A:** `zluda` is a library that tries to crossport CUDA support to AMD cards. It's legal status is messy, and its maintenance is questionable, so it's not used by default, but if you can figure out setting it up, it will likely work much better than DirectML does.
- **B:** If you can swap your PC to Linux (eg via dualbooting), Linux drivers for AMD are much more reliable than Windows one, the `ROCm` backend will install by default on a Linux install of Swarm and should work (relatively) well.
- **C:** If you don't want to swap to Linux, Windows can install `WSL` ("Windows Subsystem for Linux") which gives you Linux drivers within a Windows environment. It's a bit weird to setup, but not too hard, and probably easier than dualbooting. It similarly to real Linux should let you install Swarm in the WSL env and use ROCm drivers.

## I Have An Error Message And Don't Know What To Do

Step 1 is read the error message. A lot of error messages in Swarm are intentionally written in clear plain English to tell you exactly what went wrong and how to fix it. Sometimes it's not clear enough or you'll get an internal error without good info, so:

Step 2 is copy/paste the error message and search [GitHub](https://github.com/mcmonkeyprojects/SwarmUI/issues) and [Discord](https://discord.gg/q2y38cqjNw). Chances are you'll find somebody else has posted the same error message before and gotten an answer explaining what to do.

If both of those don't get you an instant easy answer, time for a bit more effort: Step 3 is go to `Server` -> `Logs` -> probably set `ViewType` to `Debug`, and look over the error details. Sometimes the full details of an error can give you the info you need to solve it yourself.

If you can't solve it yourself from there, Step 4 is either post on [GitHub](https://github.com/mcmonkeyprojects/SwarmUI/issues) or [Discord](https://discord.gg/q2y38cqjNw) (in `#help-forum`). Make sure you copy the full Debug log and include it (not just the bottom part, copy the whole log! The info at the top of the log when Swarm is booting up is often important), also include details about what you're trying (what paramaters are you using? What are you clicking on? Does a problem happen right away or only halfway through generating an image, or...?). It also helps to add a screenshot of your UI when the error happens.

## I Need To Install Something With Pip

So you have a pip dependency issue, eh? These usually happen from playing around with custom nodes too much, but sometimes can happen from updates. For the most part, when you stay on the "beaten path" of Swarm, this shouldn't come up. If you haven't been specifically told you need to install a pip package, you probably shouldn't do this.

When you need to install a pip dependency, you're gonna have to use the command line. The precise method depends on your OS (Windows vs Linux/Mac).

### Windows:

- Open a command line in `(Your Swarm Install)\dlbackend\comfy`
- type the command `python_embeded\python.exe -s -m pip install (your package)`
    - For example `python_embeded\python.exe -s -m pip install transformers -U` (if you got a message saying you need to reinstall `transformers`, such as the "`ImportError: huggingface-hub ...`" error message)

### Linux/Mac:

- Open a command line in `(Your Swarm Install)/dlbackend/ComfyUI/`
- activate the venv with `source venv/bin/activate`
- type the command `python -s -m pip install (your package)`
    - For example `python -s -m pip install transformers -U`

### Note

If you're an advanced user familiar with command line usage and/or with a custom python env, you can adapt the specifics as needed, just make note of the `python -s -m pip` syntax: that `-s` tells python to store the installed package in your current env. Without this (if you eg use just `pip install ...`) it may link to packages that are in your OS global install, which tends to cause a lot of issues. So, avoid that with `-s`.

## Other

If you have some other troubleshooting issue you think should be listed here, let me know on [the SwarmUI Discord](https://discord.gg/q2y38cqjNw).
