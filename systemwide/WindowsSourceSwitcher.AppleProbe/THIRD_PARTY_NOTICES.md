# Experimental Apple source probe notices

This adapter is part of WindowsSourceSwitcher under the repository's GPL-3.0 license. The following upstream portions retain their original licenses and attribution.

## AMExclusive observation approach and format layout

Source: https://github.com/AntiOblivionis/AMExclusive

Pinned commit: `c67c2f3799350ea0394d8522b3422660897a55d5`

Copyright (c) 2026 AMExclusive contributors. MIT License, reproduced in [notices/AMExclusive-LICENSE.txt](notices/AMExclusive-LICENSE.txt).

The Apple AudioConverter ABI, exported-function hook selection and ALAC cookie offsets were adapted from `mod/src/HookDll.cpp` and `mod/src/ApplePrivateOffsets.h`. This version is substantially changed: source-only evidence parsing, separate source/destination rates, strict cookie validation, drop-safe bounded observation/lifetimes, no renderer, no PCM capture, no quality locking, no UI injection and no Windows COM hook. It must not be represented as the upstream AMExclusive implementation or its validated renderer.

## MinHook v1.3.4

Source: https://github.com/TsudaKageyu/minhook

Pinned commit: `c3fcafdc10146beb5919319d0683e44e3c30d537`

Copyright (C) 2009–2017 Tsuda Kageyu; portions copyright (c) 2008–2009 Vyacheslav Patkov. BSD-style source and binary redistribution terms, including the Hacker Disassembler Engine notice, are reproduced in [third_party/minhook/LICENSE.txt](third_party/minhook/LICENSE.txt). Vendored source headers retain their notices. No network fetch occurs during our native build.
