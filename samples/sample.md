# MdToPdf Feature Tour

A dependency-free **Markdown → PDF** converter. It supports *italic*, **bold**, ***both***,
~~strikethrough~~, `inline code`, [links](https://example.com), autolinks like
<https://github.com>, bare URLs such as https://dotnet.microsoft.com, and escaped \*stars\*.
Swedish works too: Räksmörgås, åäö ÅÄÖ — “smart quotes” … and the € sign.

This line ends with two spaces  
so this one starts on a new line. Entities: &copy; &amp; &lt;tag&gt; &#8482;

Jump to the [table section](#tables) or the [code section](#code).

## Lists

- First bullet with **bold** text
- Second bullet that is long enough to wrap onto the next line, so we can check that the hanging indent lines up nicely under the text
  - Nested bullet
  - Another nested one
    1. Deeply nested ordered
    2. Second
- Back at the top level

1. Ordered item
2. Ordered item with a continuation paragraph

   This paragraph belongs to item two.
3. Third

## Quotes

> A blockquote with *emphasis* and a [link](https://example.org).
>
> Second paragraph in the quote.
>
> > Nested quote.

## Code

```csharp
// Fenced code block
static int Fib(int n) => n < 2 ? n : Fib(n - 1) + Fib(n - 2);
Console.WriteLine(Fib(20)); // a very long line that will need to be hard-wrapped because it is far wider than the page column allows
```

    Indented code block
    second line

## Tables

| Feature        | Status | Notes                                      |
|:---------------|:------:|-------------------------------------------:|
| Headings       |   ✓    | H1–H6, ATX and setext                      |
| Emphasis       |  yes   | `*`, `_`, `**`, `__`, `~~`                 |
| Tables         |  yes   | Alignment, wrapping, header repeats on new pages |
| Images         |  yes   | JPEG and PNG (incl. alpha)                 |

Setext Heading
--------------

Some text, then a rule:

---

## Images

![Transparent PNG](alpha.png)

![Gradient JPEG](gradient.jpg)

![Missing image](does-not-exist.png)

### Deep heading level 3

#### Level 4

##### Level 5

###### Level 6

<!-- this HTML comment is skipped -->

The end.
