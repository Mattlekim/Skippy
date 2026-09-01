# Skippy — a screenshot of every commit

The whole history, built and rendered headless from seed **1337** (Desktop head, 1280×720), in chronological order — from the bouncing-square starter to the chunk-streamed planet.

**55 commits · 52 rendered · 3 did not build as recorded.**

> Each shot is the game's *default spawn view*, so it's a like-for-like comparison. From the mid-history retexture on, that default sits zoomed into equatorial jungle, so features that live elsewhere (snow at the poles, clouds when zoomed out) don't all show at this fixed frame.

An interactive version of this reel was also produced as a Claude artifact.


---


### 01 · `d3b4e99` — Scaffold MonoGame multi-platform architecture (PC, Android, browser)

*2026-08-29 16:53*


> ⚠️ **Did not build at this commit.** Desktop project file had a malformed XML comment at this commit — the PC head couldn't build. Fixed in the next commit (same bouncing-square starter).


### 02 · `f3c1d52` — Add .NET SessionStart hook; fix & verify KNI browser build end-to-end

*2026-08-29 17:21*


![commit f3c1d52](002_f3c1d52.png)


### 03 · `0edefc0` — Add isometric chunked world engine (terrain, biomes, vegetation)

*2026-08-29 18:18*


![commit 0edefc0](003_0edefc0.png)


### 04 · `6ee65de` — Smooth AoE2-style terrain + temperature in degrees

*2026-08-29 18:41*


![commit 6ee65de](004_6ee65de.png)


### 05 · `8d4ae0c` — Add slope lighting and per-tile color variation to terrain

*2026-08-29 19:02*


![commit 8d4ae0c](005_8d4ae0c.png)


### 06 · `0837bdb` — Bigger tiles and AoE2-style multi-tree scatter

*2026-08-29 20:24*


![commit 0837bdb](006_0837bdb.png)


### 07 · `469946d` — Blend terrain across tile edges (no hard biome borders)

*2026-08-29 20:34*


![commit 469946d](007_469946d.png)


### 08 · `5c0d068` — Painted plant art, rivers, shoreline surf, and a climate rebalance

*2026-08-30 00:18*


![commit 5c0d068](008_5c0d068.png)


### 09 · `c34fea9` — Merge PR #1: MonoGame multi-platform setup + isometric world engine

*2026-08-30 10:12*


![commit c34fea9](009_c34fea9.png)


### 10 · `e2b59f5` — CI: fix web/Android jobs (storage + EOL SDK)

*2026-08-30 09:30*


![commit e2b59f5](010_e2b59f5.png)


### 11 · `894ab39` — Merge PR #2: CI fixes for web/Android jobs

*2026-08-30 10:31*


![commit 894ab39](011_894ab39.png)


### 12 · `751b3de` — CI: opt out of EOL-workload check for Android build

*2026-08-30 09:37*


![commit 751b3de](012_751b3de.png)


### 13 · `2be0848` — Merge PR #3: Android EOL-workload CI fix

*2026-08-30 10:37*


![commit 2be0848](013_2be0848.png)


### 14 · `dec2191` — CI: deploy Pages via gh-pages branch (no Actions storage)

*2026-08-30 09:42*


![commit dec2191](014_dec2191.png)


### 15 · `7ebdff0` — Merge PR #4: storage-free gh-pages deploy

*2026-08-30 10:42*


![commit 7ebdff0](015_7ebdff0.png)


### 16 · `1bf0bbd` — CI: pin Android job to .NET 8 SDK

*2026-08-30 09:46*


![commit 1bf0bbd](016_1bf0bbd.png)


### 17 · `cb8e880` — Merge PR #5: pin Android CI to .NET 8 SDK

*2026-08-30 10:46*


![commit cb8e880](017_cb8e880.png)


### 18 · `b381a83` — CI: fix YAML parse error in Android SDK-pin step

*2026-08-30 09:49*


![commit b381a83](018_b381a83.png)


### 19 · `8d7c90a` — Merge PR #6: fix workflow YAML parse error

*2026-08-30 10:50*


![commit 8d7c90a](019_8d7c90a.png)


### 20 · `06301df` — Android: drop missing ic_launcher icon reference

*2026-08-30 09:54*


![commit 06301df](020_06301df.png)


### 21 · `d0a06e8` — Merge PR #7: fix Android launcher-icon build error

*2026-08-30 10:54*


![commit d0a06e8](021_d0a06e8.png)


### 22 · `655d999` — Forest floor texture, denser forest cores, fewer frost bushes

*2026-08-30 10:21*


![commit 655d999](022_655d999.png)


### 23 · `ebd8a7a` — Merge PR #8: forest floor, denser cores, fewer frost bushes

*2026-08-30 11:21*


![commit ebd8a7a](023_ebd8a7a.png)


### 24 · `bd67361` — Add mountains and cliffs to terrain generation (#9)

*2026-08-30 11:45*


![commit bd67361](024_bd67361.png)


### 25 · `597196e` — Tilt the camera back and add ground shadows (#10)

*2026-08-30 11:58*


![commit 597196e](025_597196e.png)


### 26 · `354e031` — Give high elevation layers more vertical definition (#11)

*2026-08-30 12:09*


![commit 354e031](026_354e031.png)


### 27 · `0f75214` — Light the world with a moving sun, and add clouds and cloud shadows (#12)

*2026-08-30 12:58*


![commit 0f75214](027_0f75214.png)


### 28 · `5b7e35c` — Ease the height step between elevation levels (#13)

*2026-08-30 13:08*


![commit 5b7e35c](028_5b7e35c.png)


### 29 · `0d6c360` — Quarter the height step, quadruple the elevation levels (#14)

*2026-08-30 13:15*


![commit 0d6c360](029_0d6c360.png)


### 30 · `3c31e3e` — Subdivide land tiles and smooth the height between corners (#15)

*2026-08-30 13:25*


![commit 3c31e3e](030_3c31e3e.png)


### 31 · `74bd6dc` — Texture the ground with painted per-biome tiles (#16)

*2026-08-30 13:57*


![commit 74bd6dc](031_74bd6dc.png)


### 32 · `a4a1552` — Blend ground textures where biomes meet (#17)

*2026-08-30 14:08*


![commit a4a1552](032_a4a1552.png)


### 33 · `564bef9` — Erode the coastline instead of fading the shore (#18)

*2026-08-30 14:21*


![commit 564bef9](033_564bef9.png)


### 34 · `c6db891` — Round the shore by cutting coastal tiles along the waterline (#19)

*2026-08-30 14:33*


![commit c6db891](034_c6db891.png)


### 35 · `6c5d994` — Blend biomes along the climate contour, like the shore (#20)

*2026-08-30 14:46*


![commit 6c5d994](035_6c5d994.png)


### 36 · `ef59401` — Fix broken coasts after the biome-blend change (#21)

*2026-08-30 14:56*


![commit ef59401](036_ef59401.png)


### 37 · `8d43b3f` — Blend the land on shore tiles too, not just cut the water (#22)

*2026-08-30 15:03*


![commit 8d43b3f](037_8d43b3f.png)


### 38 · `17dc69c` — Soft-blend biome textures so edge-aligned borders blend too (#23)

*2026-08-30 15:14*


![commit 17dc69c](038_17dc69c.png)


### 39 · `2f31464` — Remove T-junction seams by subdividing all land tiles equally (#24)

*2026-08-30 15:23*


![commit 2f31464](039_2f31464.png)


### 40 · `2734123` — Add procedural climate-driven snow overlay

*2026-08-30 15:11*


![commit 2734123](040_2734123.png)


### 41 · `df5b822` — Cap trees and bushes with snow to match the ground

*2026-08-30 15:20*


![commit df5b822](041_df5b822.png)


### 42 · `1804014` — Procedural climate-driven snow on ground and plants (#25)

*2026-08-30 16:32*


![commit 1804014](042_1804014.png)


### 43 · `5c2f393` — fixesc

*2026-08-31 00:03*


![commit 5c2f393](043_5c2f393.png)


### 44 · `ec23d7f` — Merge remote-tracking branch 'origin/claude/monogame-multiplatform-setup-tfu99d' into claude/trees-bushes-3d-performance-wkh18i

*2026-08-31 11:57*


![commit ec23d7f](044_ec23d7f.png)


### 45 · `a935606` — Prototype: depth-tested foliage and mobs (toggle B)

*2026-08-31 12:15*


![commit a935606](045_a935606.png)


### 46 · `7535b6d` — Merge pull request #26 from Mattlekim/claude/trees-bushes-3d-performance-wkh18i

*2026-08-31 13:35*


![commit 7535b6d](046_7535b6d.png)


### 47 · `f8211c4` — Clouds: high sky seen only when zoomed out, gusty wind, kept shadows

*2026-08-31 15:48*


![commit f8211c4](047_f8211c4.png)


### 48 · `e053c54` — Simpler flatter ground, grey rock mountains, scattered 3D rocks

*2026-08-31 16:13*


> ⚠️ **Did not build at this commit.** A duplicate `TextureAtlas.Mix` method left this commit uncompilable; corrected a few commits later.


### 49 · `a6a9031` — Merge pull request #27 from Mattlekim/claude/high-clouds-wind-shadows-yloljk

*2026-08-31 17:17*


> ⚠️ **Did not build at this commit.** Merge of the commit above — inherits the same compile error.


### 50 · `795eb6f` — Make the world a wrapping planet with poles

*2026-08-31 17:20*


![commit 795eb6f](050_795eb6f.png)


### 51 · `bcb1b56` — Merge origin/main (PR #27) into the wrapping-planet branch

*2026-08-31 17:22*


![commit bcb1b56](051_bcb1b56.png)


### 52 · `1d97bdb` — A wrapping planet with poles, and an Earth-banded climate (#28)

*2026-08-31 17:26*


![commit 1d97bdb](052_1d97bdb.png)


### 53 · `01e390a` — fixes

*2026-08-31 17:40*


![commit 01e390a](053_01e390a.png)


### 54 · `fb934e2` — work on world gen

*2026-08-31 19:33*


![commit fb934e2](054_fb934e2.png)


### 55 · `b4cd2f7` — multithreading chunk gen

*2026-08-31 23:44*


![commit b4cd2f7](055_b4cd2f7.png)
