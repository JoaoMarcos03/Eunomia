# Results 20261001-1912-DESKTOP-CQRRFMG

Machine: Microsoft Windows 10 Pro 10.0.19045 (build 19045); Intel(R) Core(TM) i7-6700 CPU @ 3.40GHz (4 cores, 8 logical); 15.9 GB; GPU Intel(R) HD Graphics 530 + NVIDIA GeForce GTX 750 Ti; 1680x1050 @ 59 Hz + 1920x1080 @ 60 Hz; scale 100%; .NET SDK 10.0.401.

## PR-01 startup (ms)

| Variant | First run | Median | p95 | Min | Max | Pass (median <= 1000) |
| --- | --- | --- | --- | --- | --- | --- |
| aot | 2884.8 | 521.4 | 544.8 | 476.3 | 546.9 | True |
| aot-2cores | 836.6 | 482.9 | 783.9 | 461.7 | 834.7 | True |
| aot-wgl | 807.4 | 491.4 | 724.8 | 484 | 764.8 | True |
| jit | 6484.9 | 2136.7 | 2221.1 | 2102.5 | 2234.8 | False |

## PR-04 scroll (ms)

| Variant | Pacing p50 | Pacing p95 | UI p95 | Render thread p95 | Frame p95 | Dropped | fps | Pass |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| default-s1 | 16.676 | 17.327 | 1.58 | 1.705 | 1.7 | 2 | 59.5 | True |
| wgl-timer1-s1 | 16.662 | 17.407 | 1.559 | 2.245 | 2.24 | 4 | 58.9 | True |
| software-timer1-s1 | 16.244 | 16.926 | 1.504 | 2.979 | 2.98 | 0 | 61.3 | True |
| default-s1.5 | 16.684 | 17.367 | 1.041 | 1.163 | 1.16 | 2 | 59.5 | True |
| wgl-timer1-s1.5 | 16.625 | 17.72 | 1.547 | 2.105 | 2.1 | 6 | 58.6 | True |
| software-timer1-s1.5 | 16.135 | 16.792 | 1.136 | 1.846 | 1.85 | 0 | 61.9 | True |
| default-s1-2cores | 16.668 | 17.282 | 1.587 | 1.912 | 1.91 | 3 | 59.4 | True |
| wgl-timer1-s1-2cores | 16.666 | 17.48 | 1.891 | 2.209 | 2.21 | 1 | 59.7 | True |
| software-timer1-s1-2cores | 16.138 | 16.941 | 1.579 | 2.99 | 2.99 | 0 | 61.6 | True |
| default-s1.5-2cores | 16.673 | 17.186 | 1.021 | 1.261 | 1.26 | 2 | 59.7 | True |
| wgl-timer1-s1.5-2cores | 16.671 | 16.942 | 0.989 | 1.248 | 1.25 | 0 | 60.1 | True |
| software-timer1-s1.5-2cores | 16.177 | 16.97 | 1.004 | 1.666 | 1.67 | 0 | 61.8 | True |
| default-timer1-s1 | 16.681 | 17.315 | 1.471 | 1.796 | 1.8 | 1 | 59.9 | True |

## PR-16 idle memory

| Variant | Median working set (MB) | Max (MB) | Pass (<= 150 MB) |
| --- | --- | --- | --- |
| default | 97.6 | 98.2 | True |
| wgl | 121.8 | 121.8 | True |
