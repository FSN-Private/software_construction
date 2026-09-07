# Feature 005 ingest corpus

The upstream snapshot is Imazen [`codec-corpus` commit `8e10d4d765667c1c49d74413878fc4bfb46dcf8d`](https://github.com/imazen/codec-corpus/tree/8e10d4d765667c1c49d74413878fc4bfb46dcf8d). Its RAW/DNG suite is CC0, sourced from raw.pixls.us; its `image-rs` fixtures are MIT. The local XMP files are project-authored test data.

| File | Origin | License | SHA-256 |
|---|---|---|---|
| `sample.dng` | `raw-conformance/valid/bayer/iphone12pro.dng` | CC0-1.0 | `e91e77a4533ed7cce551d83330676ea5c47dd5e55fb38adda7819366afdbdfc2` |
| `sample.png` | `image-rs/test-images/png/16bpc/basi0g16.png` | MIT | `d354026bdd0aa6ed7bedea7c92e5eef2ebbd4854848b8a0a48f562cc71bfe80f` |
| `sample.tiff` | `image-rs/test-images/tiff/testsuite/rgb-3c-16b.tiff` | MIT | `31ff3a1f416cb7281acfbcbb4b56ee8bb94e9f91489602ff2806e5a49abc03c0` |
| `sample.jpg` | `image-rs/test-images/jpg/portrait_2.jpg` | MIT | `af6e29b89c43c85ae5c3d439d0d02c91fa1b41732387b572f9bf51139e761bc6` |

`malformed.xmp` and `ambiguous.xmp` are deterministic negative cases. `sample.dng.xmp` and `sample_01.dng.xmp` exercise the base and numbered darktable-sidecar conventions.
