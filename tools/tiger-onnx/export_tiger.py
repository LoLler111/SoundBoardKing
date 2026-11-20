"""
Rebuilds src/SoundBoardKing.Audio/Editing/Models/tiger-speech.onnx ("Separa voci") from TIGER-speech.

    git clone https://github.com/JusperLee/TIGER  <tiger>
    pip install torch torchaudio --index-url https://download.pytorch.org/whl/cpu
    pip install huggingface_hub safetensors librosa torch_complex "typeguard<3" rich pytorch_lightning onnx onnxruntime onnxslim
    python export_tiger.py <tiger> <out.onnx>

Only the network goes in (spectrum in, two separated spectra out): the app does the STFT and iSTFT, as for
the voice model (n_fft 640, hop 160, periodic Hann, center=True, 16 kHz). Fixed at 400 STFT frames
(about 4 s): tracing a longer window runs out of memory, and the app works in chunks anyway. Short paths
help on Windows: torch's own files go past 260 characters in a deep folder.
"""
import sys, math, hashlib, subprocess
from pathlib import Path
import numpy as np, torch
import torch.nn.functional as F

tiger_repo, target = sys.argv[1], Path(sys.argv[2])
sys.path.insert(0, tiger_repo)
import look2hear.models  # noqa: E402

FRAMES = 400  # SpeakerSeparator.Frames
_original_pool = F.adaptive_avg_pool1d


def _pool(x, output_size):
    """adaptive_avg_pool1d as a fixed averaging matrix: ONNX cannot pool to sizes that do not divide the input
    (the frequency path pools 67 bands down to 5)."""
    size = output_size if isinstance(output_size, int) or getattr(output_size, "ndim", 1) == 0 else output_size[0]
    length, size = int(x.shape[-1]), int(size)
    matrix = torch.zeros(length, size, dtype=x.dtype)
    for i in range(size):
        start, end = (i * length) // size, math.ceil((i + 1) * length / size)
        matrix[start:end, i] = 1.0 / (end - start)
    return torch.matmul(x, matrix)


class Net(torch.nn.Module):
    """TIGER.forward between its stft and istft: [1, 2 (re, im), 321, T] -> [1, 2 sources, 2 (re, im), 321, T]."""

    def __init__(self, model):
        super().__init__()
        self.m = model

    def forward(self, spec):
        m = self.m
        sub_ri, sub_re, sub_im, idx = [], [], [], 0
        for bw in m.band_width:
            sub_ri.append(spec[:, :, idx:idx + bw])
            sub_re.append(spec[:, 0, idx:idx + bw])
            sub_im.append(spec[:, 1, idx:idx + bw])
            idx += bw
        feats = torch.stack([m.BN[i](sub_ri[i].reshape(1, bw * 2, -1)) for i, bw in enumerate(m.band_width)], 1)
        out = m.separator(feats).reshape(1, m.nband, m.feature_dim, -1)
        est = []
        for i, bw in enumerate(m.band_width):
            o = m.mask[i](out[:, i]).reshape(1, 2, 2, m.num_output, bw, -1)
            mask = o[:, 0] * torch.sigmoid(o[:, 1])
            mr = mask[:, 0] - (mask[:, 0].sum(1, keepdim=True) - 1) / m.num_output
            mi = mask[:, 1] - mask[:, 1].sum(1, keepdim=True) / m.num_output
            re, im = sub_re[i].unsqueeze(1), sub_im[i].unsqueeze(1)
            est.append(torch.stack([re * mr - im * mi, re * mi + im * mr], 2))
        return torch.cat(est, 3)


def dedupe(path_in, path_out):
    """Merges identical initializers and large Constant tensors: tracing unrolls the weight-shared blocks."""
    import onnx
    from onnx import numpy_helper
    model = onnx.load(path_in)
    graph = model.graph
    seen, rename, keep = {}, {}, []

    def key(a):
        return a.dtype.str, a.shape, hashlib.sha1(a.tobytes()).hexdigest()

    for init in graph.initializer:
        k = key(numpy_helper.to_array(init))
        if k in seen:
            rename[init.name] = seen[k]
        else:
            seen[k] = init.name
            keep.append(init)
    nodes = []
    for node in graph.node:
        if node.op_type == "Constant" and len(node.attribute) == 1 and node.attribute[0].name == "value":
            a = numpy_helper.to_array(node.attribute[0].t)
            if a.size >= 16:
                k = key(a)
                if k in seen:
                    rename[node.output[0]] = seen[k]
                else:
                    keep.append(numpy_helper.from_array(a, name=node.output[0]))
                    seen[k] = node.output[0]
                continue
        nodes.append(node)
    for node in nodes:
        for i, name in enumerate(node.input):
            if name in rename:
                node.input[i] = rename[name]
    del graph.node[:]
    graph.node.extend(nodes)
    del graph.initializer[:]
    graph.initializer.extend(keep)
    onnx.checker.check_model(model)
    onnx.save(model, path_out)


model = look2hear.models.TIGER.from_pretrained("JusperLee/TIGER-speech").eval()
net = Net(model).eval()
x = torch.randn(1, 160 * (FRAMES - 1)) * 0.1
spec = torch.stft(x, 640, 160, window=torch.hann_window(640), return_complex=True)
spec = torch.stack([spec.real, spec.imag], 1).contiguous()
with torch.no_grad():
    reference = net(spec).numpy()

raw, merged = target.with_suffix(".raw.onnx"), target.with_suffix(".merged.onnx")
F.adaptive_avg_pool1d = _pool
torch.onnx.export(net, (spec,), str(raw), input_names=["spec"], output_names=["sources"], opset_version=17, dynamo=False)
F.adaptive_avg_pool1d = _original_pool
dedupe(raw, merged)
subprocess.run([sys.executable, "-m", "onnxslim", str(merged), str(target)], check=True)
raw.unlink()
merged.unlink()

import onnxruntime as ort  # noqa: E402
got = ort.InferenceSession(str(target)).run(None, {"spec": spec.numpy()})[0]
print(f"{target}: {target.stat().st_size} bytes, max diff from PyTorch {np.abs(got - reference).max():.2e}"
      f" (peak {np.abs(reference).max():.2f})")
