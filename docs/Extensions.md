# SwarmUI Extensions

Currently there are only a few extensions out there, so there's not much convenient extension handling, but it's easy enough to deal with manually with only a touch of technical knowledge.

In the future when more extensions exist, we'll build out more support for easily managing them.

### How To Install & Use Extensions

- Extensions are distributed as git repos
- Extensions go in `(Swarm)/src/Extensions`
- So, to install:
    - Close Swarm if it's running
    - find a repo you want, say eg `https://github.com/Quaggles/SwarmUI-FaceTools`
    - open a terminal in `(Swarm)/src/Extensions`
    - run command `git clone (URL)`, eg `git clone https://github.com/Quaggles/SwarmUI-FaceTools`
    - Run Swarm's `update-windows.bat` (or equivalent script for your OS) (this is to trigger a rebuild so it actually applies the extension)
    - Start Swarm

### How To Find Extensions

For now, manual list of known extensions:

- [FaceTools](https://github.com/Quaggles/SwarmUI-FaceTools) adds support for CodeFormer FaceRestore, and ReActor.
- [WDV](https://huggingface.co/waifu-diffusion/wdv-tests) adds support for WaifuDiffusion-V EDM sampling.

In the future if more extensions exist, there will be a better manager for these.
