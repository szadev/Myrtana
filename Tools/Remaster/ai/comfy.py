"""Minimal local ComfyUI HTTP client (stdlib only) for the remaster AI pipelines (Docs/ai-tools.md).

Everything stays on this machine: http://127.0.0.1:8188, models under C:\\AI. A GPU job is refused with < 6 GB free RAM
(SDXL + ControlNet keep ~7 GB of host memory while loaded); models are unloaded after every job.
"""
import json
import os
import subprocess
import sys
import time
import urllib.request
import uuid

HOST = 'http://127.0.0.1:8188'
COMFY = r'C:\AI\ComfyUI-portable\ComfyUI_windows_portable\ComfyUI'
INPUT = os.path.join(COMFY, 'input')
OUTPUT = r'C:\AI\output'
SDXL = 'sd_xl_base_1.0.safetensors'
ESRGAN = 'RealESRGAN_x4plus.pth'
CONTROLNET = 'controlnet-tile-sdxl-1.0.safetensors'
MIN_FREE_GB = 6.0
_warm = False   # the last job left SDXL loaded (unload=False): its memory is already taken, no new 6 GB needed


def free_ram_gb() -> float:
    o = subprocess.run(['powershell', '-NoProfile', '-c', '(Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory'],
                       capture_output=True, text=True).stdout
    return int(o.strip()) / 1024 / 1024


def alive() -> bool:
    try:
        urllib.request.urlopen(HOST + '/system_stats', timeout=3).read()
        return True
    except Exception:
        return False


def free_models():
    global _warm
    _warm = False
    req = urllib.request.Request(HOST + '/free', json.dumps({'unload_models': True, 'free_memory': True}).encode(),
                                 {'Content-Type': 'application/json'})
    urllib.request.urlopen(req).read()


def run(prompt: dict, timeout: float = 1200, unload: bool = True) -> list:
    """Queues a graph, waits, returns the output image paths (node order)."""
    global _warm
    ram = 99.0 if _warm else free_ram_gb()
    wait = float(os.environ.get('G3AI_RAM_WAIT', '600'))   # seconds to wait for memory before giving up
    for k in range(int(wait / 10)):
        if ram >= MIN_FREE_GB:
            break
        if k % 30 == 0:
            print(f'free RAM {ram:.1f} GB < {MIN_FREE_GB} GB: waiting', file=sys.stderr, flush=True)
        time.sleep(10)
        ram = free_ram_gb()
    if ram < MIN_FREE_GB:
        raise RuntimeError(f'free RAM {ram:.1f} GB < {MIN_FREE_GB} GB: not starting a GPU job')
    body = json.dumps({'prompt': prompt, 'client_id': str(uuid.uuid4())}).encode()
    r = json.load(urllib.request.urlopen(urllib.request.Request(HOST + '/prompt', body, {'Content-Type': 'application/json'})))
    if r.get('node_errors'):
        raise RuntimeError('ComfyUI node errors: ' + json.dumps(r['node_errors'])[:2000])
    pid = r['prompt_id']
    t0 = time.time()
    while True:
        time.sleep(1)
        h = json.load(urllib.request.urlopen(f'{HOST}/history/{pid}'))
        if pid in h:
            st = h[pid].get('status', {})
            if st.get('status_str') == 'error':
                raise RuntimeError('ComfyUI error: ' + json.dumps(st.get('messages'))[:2000])
            if st.get('completed'):
                break
        if time.time() - t0 > timeout:
            raise RuntimeError('ComfyUI timeout')
    if unload:
        free_models()
    else:
        _warm = True
    files = []
    for node in sorted(h[pid]['outputs'], key=lambda k: int(k)):
        for f in h[pid]['outputs'][node].get('images', []):
            files.append(os.path.join(OUTPUT, f.get('subfolder', ''), f['filename']))
    return files


def img2img_graph(tile_names: list, tag: str, pos: str, neg: str, denoise: float, seed: int, steps: int, cfg: float,
                  cn_strength: float, cn_end: float) -> dict:
    """One graph, one SDXL img2img per tile (each tile its own ControlNet Tile hint when cn_strength > 0)."""
    g = {
        '1': {'class_type': 'CheckpointLoaderSimple', 'inputs': {'ckpt_name': SDXL}},
        '2': {'class_type': 'CLIPTextEncode', 'inputs': {'clip': ['1', 1], 'text': pos}},
        '3': {'class_type': 'CLIPTextEncode', 'inputs': {'clip': ['1', 1], 'text': neg}},
    }
    if cn_strength > 0:
        g['4'] = {'class_type': 'ControlNetLoader', 'inputs': {'control_net_name': CONTROLNET}}
    for k, name in enumerate(tile_names):
        b = 10 + k * 10
        g[str(b)] = {'class_type': 'LoadImage', 'inputs': {'image': name}}
        g[str(b + 1)] = {'class_type': 'VAEEncode', 'inputs': {'pixels': [str(b), 0], 'vae': ['1', 2]}}
        pos_c, neg_c = ['2', 0], ['3', 0]
        if cn_strength > 0:
            g[str(b + 5)] = {'class_type': 'ControlNetApplyAdvanced', 'inputs': {
                'positive': ['2', 0], 'negative': ['3', 0], 'control_net': ['4', 0], 'image': [str(b), 0],
                'strength': cn_strength, 'start_percent': 0.0, 'end_percent': cn_end, 'vae': ['1', 2]}}
            pos_c, neg_c = [str(b + 5), 0], [str(b + 5), 1]
        g[str(b + 2)] = {'class_type': 'KSampler', 'inputs': {
            'model': ['1', 0], 'positive': pos_c, 'negative': neg_c, 'latent_image': [str(b + 1), 0],
            'seed': seed + k, 'steps': steps, 'cfg': cfg, 'sampler_name': 'dpmpp_2m', 'scheduler': 'karras',
            'denoise': denoise}}
        g[str(b + 3)] = {'class_type': 'VAEDecode', 'inputs': {'samples': [str(b + 2), 0], 'vae': ['1', 2]}}
        g[str(b + 4)] = {'class_type': 'SaveImage', 'inputs': {'images': [str(b + 3), 0], 'filename_prefix': f'g3ai/{tag}_t{k}'}}
    return g
