# Advanced Usage

(TODO: More examples of advanced usage with explanation)

# Accessing SwarmUI From Other Devices

- To access SwarmUI from another device over LAN:
    - Simply open SwarmUI to the `Server` -> `Server Configuration` tab, find `Host` (default value is `localhost`) and change the value to `0.0.0.0`, then save and restart
        - Note you may also need to allow SwarmUI through your firewall.
- To access SwarmUI over open internet without port forwarding:
    - You can either launch use Cloudflared or Ngrok
        - For **Cloudflared:** Install Cloudflared according to [their readme](https://github.com/cloudflare/cloudflared?tab=readme-ov-file#installing-cloudflared) (note: ignore the stuff about accounts/domains/whatever, only the `cloudflared` software install is relevant), and launch SwarmUI with `--cloudflared-path [...]` or set the path in Server Configuration `CloudflaredPath` option and restart
            - For Debian Linux servers, look at how the [Colab Notebook](/colab/colab-notebook.ipynb) installs and uses cloudflared.
        - For **ngrok:**  Install ngrok according to [their documentation](https://ngrok.com/) and login to your ngrok account, and launch SwarmUI with `--ngrok-path [...]`

## Custom Workflows (ComfyUI)

So, all those parameters aren't enough, you want MORE control? Don't worry, we got you covered, with the power of raw ComfyUI node graphs!

- Note that this requires you use a ComfyUI backend.
- At the top, click the `Comfy Workflow Editor` tab
- Use [the full power of ComfyUI](https://comfyanonymous.github.io/ComfyUI_examples/) at will to build a workflow that suites your crazy needs.
- You can generate images within comfy while you're going.
- If you have weird parameters, I highly recommend creating `Primitive` nodes and setting their title to something clear, and routing them to the inputs, so you can recognize them easily later.
- Once you're done, make sure you have a single `Save Image` node at the end, then click the `Use This Workflow` button

![img](/docs/images/usecomfy.png)

- Your parameter listing is now updated to parameters that are in your workflow. Recognized ones use their default parameter view, other ones get listed on their own with Node IDs or titles.
- You can now generate images as normal, but it will automatically use your workflow. This applies to all generation features, including the Grid Generator tool - which has its axes list automatically updated to the workflow parameter list!
- If you update the workflow in the comfy tab, you have to click `Use This Workflow` again to load your changes.
- If you want to go back to normal and remove the comfy workflow, make sure your parameters list is scrolled up, as there's a `Disable Custom Comfy Workflow` button you can click there.

More thorough information about custom Comfy Workflows will be in [Features/Custom Comfy Workflows](/docs/Features/Comfy-Workflows.md).

## Triton, TorchCompile, SageAttention on Windows

- If on Linux, probably just:
    - Follow [Troubleshooting Pip Install](/docs/Troubleshooting.md#i-need-to-install-something-with-pip) to pip install `triton sageattention`
    - and then edit the Backend to have `--use-sage-attention` under `ExtraArgs`
    - and maybe just works?
- For Windows, it's a lot more effort, see below:

This is only for very advanced / tech-skilled users. Normal users beware, here be cyberdragons.

Triton is a Linux-only AI acceleration library that you can hack into working on Windows. It enables `Torch.Compile` params and things like that. `SageAttention` is an acceleration tool that depends on Triton.

- First, follow steps 5 and 6 of Triton-Windows install guide (MSVC, VCRedist) https://github.com/woct0rdho/triton-windows?tab=readme-ov-file#5-msvc-and-windows-sdk
    - See also the GPU-specific notes at the top of the readme
- Open a command line in `(Your Swarm Install)\dlbackend\comfy`
    - type the command `python_embeded\python.exe --version`
    - Install a global python of the exact same version (eg mine is `Python 3.11.8`, so I had to install a global `3.11.8`)
- Open a new terminal not in any specific location
    - Type `python --version`, make sure it matches. If not you'll have to clean up your env path.
    - Type `python -m pip install triton-windows`
    - Type `where python` and open the folder for the python exe it gives you, for example mine was `C:\Users\my_user_name\AppData\Local\Programs\Python\Python311\`
    - Also open a second folder window of `(Your Swarm Install)\dlbackend\comfy\python_embeded`
    - Copy over the `Libs` (with an 's') folder from the global python to the 'embeded' python
    - Copy over the contents of the `Include` folder too
- Go back to the command line inside the `dlbackend\comfy`
    - `.\python_embeded\python.exe -s -m pip install triton-windows`
    - `.\python_embeded\python.exe -s -m pip install sageattention`
- Launch SwarmUI
    - Go to Server->Backends, edit the Comfy Self Start backend
    - Under `ExtraArgs`, add `--use-sage-attention`
    - Save the backend, let it load, go back to Generate tab
    - Generate something. Let it finish, then generate a second thing.
    - Hopefully it works fine and is a bit faster than usual. If not, ~~god help you~~ maybe ask for help on the Swarm Discord. No promises. This stuff is a mess.
    - Optionally go to Advanced->Advanced Model Addons->set `Torch Compile` to `inductor`
        - This will be much slower on the first run of any model, then subsequent runs will be faster.
        - That means TorchCompile is only faster if you're going to generate many things in sequence. It's maybe a 30% speedup, but 1-2 minutes added to the first run.
        - So for example Flux Dev on my 4090 with sageattention takes 10 seconds to generate 1 image, with TorchCompile it's 7 seconds. That's 3 seconds cut per gen but 20 x 3 seconds added to first run, meaning I have to generate at least 20 images in a row just to pull even on gen speeds. Arguably with slow iterations (trying different things one at a time) it will still "feel" faster and thus be worth using anyway. Up to personal choice.
