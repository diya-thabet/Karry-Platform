---
title: "Fonts embedded in Karry documents"
area: architecture
tags: [documents, pdf, fonts, licence]
source: "ADR-0040 §1; P2.11 slice 1"
status: live
last_updated: 2026-11-26
---

# Fonts embedded in Karry documents

Every PDF Karry issues embeds these fonts, never the server's own (ADR-0040 §1): a report looks the same wherever it
is generated, and no font with a restrictive licence ends in a document.

| File | Font | Licence | Source |
|---|---|---|---|
| `NotoSans-Regular.ttf` | Noto Sans Regular (hinted TrueType) | SIL Open Font License 1.1 ([OFL.txt](OFL.txt)) | [notofonts/notofonts.github.io](https://github.com/notofonts/notofonts.github.io), `fonts/NotoSans/hinted/ttf` |
| `NotoSans-Bold.ttf` | Noto Sans Bold (hinted TrueType) | SIL Open Font License 1.1 ([OFL.txt](OFL.txt)) | same |

The OFL allows embedding and redistribution with the licence kept beside the files. Noto Sans covers Latin, Greek and
Cyrillic; Arabic and N'Ko names will need Noto Sans Arabic and Noto Sans N'Ko when reports are issued in those
scripts.
