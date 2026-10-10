# Resultados 20261001-0204-DESKTOP-CQRRFMG

Máquina: Microsoft Windows 10 Pro 10.0.19045 (build 19045); Intel(R) Core(TM) i7-6700 CPU @ 3.40GHz (4 núcleos, 8 lógicos); 15.9 GB; GPU Intel(R) HD Graphics 530 + NVIDIA GeForce GTX 750 Ti; ecrãs 1680x1050 @ 59 Hz + 1920x1080 @ 60 Hz; escala 100%; .NET SDK 10.0.401.

## PR-01 arranque (ms)

| Variante | 1.ª corrida | Mediana | p95 | Mín. | Máx. | Passa (mediana <= 1000) |
| --- | --- | --- | --- | --- | --- | --- |
| aot | 3029.2 | 498.5 | 842.6 | 475.7 | 847 | True |
| aot-2cores | 479.7 | 511.7 | 820.9 | 465 | 891.5 | True |
| aot-wgl | 824.2 | 482.6 | 686 | 471.6 | 790.2 | True |
| jit | 5863.1 | 2041.6 | 2063.4 | 2017.5 | 2066.9 | False |

## PR-04 scroll (ms)

| Variante | Pacing p50 | Pacing p95 | UI p95 | Render thread p95 | Frame p95 | Perdidos | fps | Passa |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| default-s1 | 19.171 | 66.202 | 1.555 | 2.23 | 2.23 | 120 | 33 | False |
| wgl-timer1-s1 | 16.116 | 16.807 | 1.545 | 1.851 | 1.85 | 0 | 61.8 | True |
| software-timer1-s1 | 16.129 | 16.818 | 1.561 | 2.77 | 2.77 | 0 | 61.8 | True |
| default-s1.5 | 18.954 | 59.178 | 0.95 | 1.103 | 1.1 | 117 | 34.3 | False |
| wgl-timer1-s1.5 | 16.363 | 16.897 | 0.899 | 0.996 | 1 | 1 | 61.2 | True |
| software-timer1-s1.5 | 16.191 | 16.937 | 0.927 | 1.713 | 1.71 | 0 | 61.6 | True |
| default-s1-2cores | 18.989 | 71.264 | 1.587 | 2.067 | 2.07 | 123 | 33.8 | False |
| wgl-timer1-s1-2cores | 16.118 | 16.892 | 1.19 | 1.572 | 1.57 | 1 | 61.7 | True |
| software-timer1-s1-2cores | 16.147 | 16.929 | 1.487 | 2.747 | 2.75 | 1 | 61.7 | True |
| default-s1.5-2cores | 18.877 | 60.765 | 0.941 | 1.05 | 1.05 | 117 | 34.3 | False |
| wgl-timer1-s1.5-2cores | 16.355 | 16.892 | 0.808 | 1.051 | 1.05 | 0 | 61.4 | True |
| software-timer1-s1.5-2cores | 16.204 | 16.979 | 0.909 | 1.676 | 1.68 | 1 | 61.6 | True |
| default-timer1-s1 | 19.139 | 72.819 | 1.782 | 2.211 | 2.21 | 117 | 32.8 | False |

## PR-16 memória em repouso

| Variante | Working set mediana (MB) | Máx. (MB) | Passa (<= 150 MB) |
| --- | --- | --- | --- |
| omissão | 97.4 | 98 | True |
| wgl | 124.7 | 124.7 | True |
