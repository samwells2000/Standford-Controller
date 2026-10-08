# Quick reference card

`quick-reference.html` is the source of [`../Tempo-Quick-Reference.pdf`](../Tempo-Quick-Reference.pdf), the
two-sided A4 card for new users: getting started on side 1, everyday tasks on side 2. The program carries the
PDF and opens it from **Help ▸ Quick reference card (PDF)**, and every release attaches it.

To rebuild the PDF after changing the card or the program:

```sh
node docs/quick-reference/build.mjs      # needs Playwright with Chromium
```

The card uses the program's own fonts (IBM Plex) from `src/StanfordController.App/Assets/Fonts`.

`main-window.jpg` is the smoke test's `03-delays.png` screenshot (`Tempo --smoke-test <folder> --headless`)
with the timing diagram band cut out, so that the whole window fits on the first side. If the window
changes, replace the screenshot and check that the numbered pins still point at the right places. The card
must stay at exactly two A4 pages, one sheet printed double-sided; the build prints a warning and fails if it does
not. Each side keeps a 12 mm margin (the padding of `.page`) so that nothing falls outside the area a printer can
reach. Print it at actual size (100%) rather than "fit to page", flipping on the long edge.
