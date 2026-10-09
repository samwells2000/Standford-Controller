# Running the Linux build on your own Ubuntu server

GitHub Actions can run a job on a machine you own (a *self-hosted runner*) instead of one of GitHub's.
The **Linux** job of `build-and-release.yml` (build, unit tests, UI smoke test, `.deb`, tarball and
PKGBUILD) can run on an Ubuntu server this way. The Windows and macOS jobs stay on GitHub, because the
smoke test has to run on each real system and the Mac app is put together with Apple-only tools
(`codesign`, `iconutil`, `ditto`).

Nothing changes until you set the repository variable in step 5, and deleting it moves the job back to
GitHub, so you can try this safely.

## What you need

- Ubuntu Server 22.04 or 24.04, x86-64, with internet access (the runner only makes outgoing HTTPS
  connections to GitHub; no ports need opening).
- About 2 CPU cores, 4 GB RAM and 10 GB of free disk. More cores make the build faster.
- `sudo` on the server, and admin rights on the GitHub repository.
- The repository must stay **private** while the runner is attached. On a public repository anyone who
  opens a pull request could run code on your server.

## 1. Install the packages the build needs

The job runs without root, so the server needs these installed once:

```sh
sudo apt-get update
sudo apt-get install -y curl git tar xvfb xauth libice6 libsm6 libfontconfig1 fonts-dejavu-core \
  libicu-dev ca-certificates
```

`xvfb` gives the smoke test a virtual screen on a server with no monitor. `libicu-dev` pulls in the ICU
library .NET needs. You do not need to install .NET: the job downloads the version `global.json` asks for
into the runner's own folder the first time, and reuses it after that.

## 2. Create a user for the runner

The runner should not run as root or as your own account:

```sh
sudo adduser --disabled-password --gecos "GitHub runner" github-runner
```

## 3. Download and register the runner

1. On GitHub open the repository, then **Settings → Actions → Runners → New self-hosted runner**.
2. Choose **Linux** and **x64**. GitHub shows a *Download* block and a *Configure* block with a one-hour
   token. Keep that page open.
3. On the server, switch to the runner user and make a folder for it:

   ```sh
   sudo -iu github-runner
   mkdir actions-runner && cd actions-runner
   ```

4. Paste and run the commands from GitHub's **Download** block (`curl -o actions-runner-linux-x64-…`,
   the checksum line and `tar xzf …`).
5. Register it, using the token from GitHub's **Configure** block. The `tempo` label is what the workflow
   looks for:

   ```sh
   ./config.sh --url https://github.com/samwells2000/Standford-Controller \
     --token PASTE_THE_TOKEN_HERE --name tempo-ubuntu --labels tempo --unattended
   exit
   ```

## 4. Run it as a service

So the runner starts on boot and keeps running after you log out:

```sh
cd /home/github-runner/actions-runner
sudo ./svc.sh install github-runner
sudo ./svc.sh start
sudo ./svc.sh status
```

The status should say `active (running)`, and on GitHub under **Settings → Actions → Runners** the runner
`tempo-ubuntu` should show as **Idle**.

## 5. Send the Linux job to it

On GitHub open **Settings → Secrets and variables → Actions → Variables → New repository variable** and add:

| Name | Value |
| --- | --- |
| `LINUX_RUNNER` | `self-hosted` |

## 6. Check it works

Open **Actions → Build and release → Run workflow**, pick `main` and run it. Open the **Linux** job: the
*Set up job* step should say `Runner name: 'tempo-ubuntu'`. The first run takes a few minutes longer
because it downloads .NET and the NuGet packages; later runs reuse them.

## Day to day

- **The server has to be on.** If it is off, the Linux job waits in the queue (GitHub gives up after
  24 hours) and the release on `main` waits with it. If the server will be off for a while, delete the
  `LINUX_RUNNER` variable and the job runs on GitHub again.
- **Updates.** The runner updates itself. Keep Ubuntu updated as usual (`sudo apt-get upgrade`).
- **Logs.** `sudo journalctl -u 'actions.runner.*' -f` shows what the runner is doing.
- **Disk space.** Each run cleans its working folder. .NET and the NuGet cache stay in
  `/home/github-runner`, about 2 GB.
- **Removing it.** Get a removal token from the runner's page on GitHub, then:

  ```sh
  cd /home/github-runner/actions-runner
  sudo ./svc.sh stop && sudo ./svc.sh uninstall
  sudo -u github-runner ./config.sh remove --token PASTE_THE_TOKEN_HERE
  ```

## What this saves

GitHub bills private-repository minutes with multipliers: Linux 1×, Windows 2×, macOS 10×. The Linux job
is the cheapest of the four, so moving it saves the least; the Mac jobs are where most minutes go. Two other
changes in the workflow save more:

- A branch with a pull request is built once for the pull request, not a second time for the push.
- Build outputs are kept for 3 days and smoke test screenshots for 7, instead of 30 and 14. Releases are
  stored separately and do not count against the Actions artifact storage limit.

GitHub announced a $0.002 per minute charge for self-hosted runners on private repositories from March 2026,
then postponed it. Check GitHub's Actions billing page for the current position.

To move the Windows or macOS jobs off GitHub too, you would need a Windows PC and a Mac (a Mac can host all
three systems: macOS natively, Linux in Docker and Windows for Arm in a virtual machine), each with its own
runner registered the same way.
