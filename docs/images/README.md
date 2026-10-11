# User guide pictures

The pictures in [`../USER-GUIDE.md`](../USER-GUIDE.md) are drawn from [`diagrams.html`](diagrams.html): each
`<figure id="name">` becomes `name.png` and, for the dark theme, `name.dark.png`. The program builds them in and
shows the dark version when the dark theme is on.

To redraw them after changing `diagrams.html`:

```sh
node docs/images/build.mjs      # needs Playwright with Chromium
```

The figures use the program's own fonts and colours, and are saved at twice the size they are shown so they stay
sharp on high-resolution screens.

`src/main-window-light.png` and `src/main-window-dark.png` are the smoke test's `03-delays.png` and
`03-delays-dark.png` screenshots (`Tempo --smoke-test <folder> --headless`). If the main window changes, replace
them, check that the numbered pins in the `main-window` figure still point at the right places, and keep the
numbered list under *The main window* in the guide in step.
