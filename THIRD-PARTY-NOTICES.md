# Third-party notices

CBIX ships third-party components inside its container image, including **pre-compiled native
binaries**. The licences below carry attribution obligations that attach to *binary
redistribution* — not merely to source distribution — so this file exists to satisfy them and must
travel with any built artifact.

This file lists components whose licence requires notice when redistributed. It is not a full
dependency inventory: `packages.lock.json` in each project is the authoritative resolved graph, and
`Directory.Packages.props` records why each direct dependency was chosen and pinned.

**Maintenance.** Update this file whenever a package with an attribution obligation is added,
removed, or moved to a version whose bundled components change. The native rendering stack is the
part most likely to shift without a visible NuGet-level signal: `bblanchon.PDFium` republishes with
a new upstream PDFium build, and PDFium's own bundled third-party components can change between
Chromium releases.

---

## Managed components

### PDFtoImage

- **Version:** 5.3.0
- **Licence:** MIT
- **Copyright:** © David Sungaila
- **Source:** https://github.com/sungaila/PDFtoImage
- **Used for:** rasterising PDF pages for the generic-vision document-content profile.

> MIT License
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction,
> including without limitation the rights to use, copy, modify, merge, publish, distribute,
> sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or
> substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
> NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
> NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
> DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT
> OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### SkiaSharp

- **Version:** 4.150.1 (managed assembly plus the `SkiaSharp.NativeAssets.*` packages for Windows,
  Linux and macOS)
- **Licence:** MIT
- **Copyright:** © Microsoft Corporation; © Xamarin, Inc.
- **Source:** https://github.com/mono/SkiaSharp
- **Used for:** the bitmap and PNG-encoding surface behind page rasterisation.

The `SkiaSharp.NativeAssets.*` packages redistribute compiled **Skia** binaries. Skia is
© Google LLC and is licensed under the BSD 3-Clause licence; its own bundled third-party components
are enumerated in the Skia source tree's `third_party` directory. The BSD 3-Clause terms below apply
to those binaries.

> Copyright © 2011 Google Inc. All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted
> provided that the following conditions are met:
>
> 1. Redistributions of source code must retain the above copyright notice, this list of conditions
>    and the following disclaimer.
> 2. Redistributions in binary form must reproduce the above copyright notice, this list of
>    conditions and the following disclaimer in the documentation and/or other materials provided
>    with the distribution.
> 3. Neither the name of Google Inc. nor the names of its contributors may be used to endorse or
>    promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
> IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
> FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
> CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
> DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
> DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER
> IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT
> OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

### PdfPig

- **Version:** 0.1.15
- **Licence:** Apache License 2.0
- **Source:** https://github.com/UglyToad/PdfPig
- **Used for:** the local per-page text layer, which is the corpus the validator's grounding gate
  checks extracted snippets against.

PdfPig incorporates work derived from the Apache PDFBox project (© The Apache Software Foundation).
The Apache-2.0 licence requires that the `NOTICE` file distributed with the package be reproduced;
see the package's own `NOTICE` for the current text.

---

## Native components

### PDFium (via `bblanchon.PDFium.Win32` / `.Linux` / `.macOS`)

- **Version:** 152.0.7961 (pre-compiled PDFium binaries)
- **Packaging licence:** Apache License 2.0 — © Benoît Blanchon
- **Packaging source:** https://github.com/bblanchon/pdfium-binaries
- **Upstream licence:** PDFium is © The PDFium Authors and © Google Inc., licensed **BSD 3-Clause**
- **Upstream source:** https://pdfium.googlesource.com/pdfium/
- **Used for:** rasterising PDF pages (arrives transitively through PDFtoImage).

These packages redistribute a compiled `libpdfium` shared library. Two licences apply: Apache-2.0 to
the packaging repository, and BSD 3-Clause (reproduced above, in the same form, for The PDFium
Authors) to the PDFium code itself.

**PDFium bundles further third-party code, and its licences travel with the binary.** The
components with their own attribution requirements include:

| Component | Licence | Copyright |
|---|---|---|
| FreeType | FreeType Licence (BSD-style, attribution required) or GPLv2 at the user's option — CBIX relies on the **FreeType Licence** option | © The FreeType Project |
| libjpeg-turbo | BSD 3-Clause / IJG | © D. R. Commander and contributors; © Thomas G. Lane (IJG) |
| libpng | PNG Reference Library Licence (zlib-style) | © the PNG Reference Library Authors |
| zlib | zlib licence | © Jean-loup Gailly and Mark Adler |
| libopenjpeg | BSD 2-Clause | © Université catholique de Louvain and contributors |
| LCMS (Little CMS) | MIT | © Marti Maria Saguer |

The FreeType Licence requires that use of FreeType be acknowledged in the documentation of any
product using it. That acknowledgement is:

> Portions of this software are copyright © The FreeType Project (https://www.freetype.org). All
> rights reserved.

The libjpeg-turbo / IJG terms require the statement:

> This software is based in part on the work of the Independent JPEG Group.

The authoritative, version-exact list for the pinned build is the `LICENSE`/`AUTHORS` set in the
PDFium source tree at the revision `bblanchon.PDFium` built from; the nuspec records the packaging
repository commit, which identifies that revision. Re-check this table when the PDFium version moves
— it is bundled native code, so its component list is not visible in the NuGet dependency graph.
