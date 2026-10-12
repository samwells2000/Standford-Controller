# DG645 reference documents

Official documents from Stanford Research Systems for the DG645 Digital Delay Generator.

| File | Document | Source |
| --- | --- | --- |
| `DG645m.pdf` | DG645 operation and service manual (remote programming is chapter 4) | <https://www.thinksrs.com/downloads/pdfs/manuals/DG645m.pdf> |
| `DG645c.pdf` | DG645 data sheet | <https://www.thinksrs.com/downloads/pdfs/catalog/DG645c.pdf> |
| `DG645_TCPIP.pdf` | Ethernet communication set-up note | <https://www.thinksrs.com/downloads/pdfs/other%20stuff/DG645_TCPIP.pdf> |

Product page: <https://www.thinksrs.com/products/dg645.html>

## Adding the PDFs to this folder

The manual is Revision 1.1 (September 2008), which is still the version Stanford Research Systems
publishes; it describes firmware 1.00.x. To fetch newer copies of the documents:

* **From GitHub:** open the repository's **Actions** tab, choose **Fetch DG645 manuals**, and click
  **Run workflow**. It downloads the documents and commits them to the branch you pick.
* **On a Windows PC:** run `scripts\Download-Manuals.ps1` from the repository folder, then commit
  the files.

A summary of the remote commands, cross-referenced with this program, is in
[`../DG645-Command-Reference.md`](../DG645-Command-Reference.md).
