---
title: Feature tour, part two
author: MdToPdf
---

# Newer features

Convert this file with `mdtopdf --toc samples\features.md` to get a table of contents as well.

## Task lists

- [x] Checked items get a ticked box
- [ ] Open items get an empty one
- An ordinary item keeps its bullet

## Reference links and footnotes

This is a [reference link][docs], and so is [the shortcut form][]. Footnotes[^one] are numbered in the
order they are first used, and the numbers jump to the notes at the end of the document.[^two]

[docs]: https://commonmark.org "CommonMark"
[the shortcut form]: https://github.github.com/gfm/

[^one]: A note can hold several paragraphs.

    This is the second paragraph of the first note.

[^two]: This one links to [another file](sample.md), which opens the PDF converted from it.

## Inline images

An image ![a transparent square](alpha.png) can sit in the middle of a line of text, and a line grows to fit it.
Linked ones work too: [![a gradient](gradient.jpg)](https://example.com).

| Inside a table | Works as well |
|---|---|
| ![square](alpha.png) | Images shrink to fit their cell. |

## Text

**Bold code like `this` stays bold**, and spacing in code like `a   b` is kept.

Entities work: &auml; &aring; &ouml; &eacute; &ldquo;quoted&rdquo; &frac12; &rarr; arrow.

Mixed emphasis is resolved the CommonMark way: **bold *and italic*** and ***both** then italic*.

A hidden comment <!-- like this one --> stays hidden, and invisible soft hyphens in co&shy;operate are dropped.
