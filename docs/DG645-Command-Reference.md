# SRS DG645 remote command reference

A working reference for the Stanford Research Systems **DG645 Digital Delay Generator** remote
interface, as implemented by this program. The authoritative source is chapter 4, *Remote
Programming*, of the DG645 operation manual; see [`manuals/`](manuals/README.md) for the official PDFs.

> **About this document.** The official manual could not be downloaded while this project was
> written, so this reference was compiled from the manual's command set and cross-checked against
> four independent, field-tested open-source DG645 drivers:
> [EPICS delaygen](https://github.com/epics-modules/delaygen),
> [QCoDeS contrib drivers](https://github.com/QCoDeS/Qcodes_contrib_drivers),
> [InstrumentKit](https://github.com/instrumentkit/InstrumentKit) and
> [Labber drivers](https://github.com/Labber-software/Drivers).
> Commands marked **†** are standard IEEE-488.2 or SRS commands that none of those drivers use;
> they follow the manual's command list but have not been cross-checked. Please confirm them
> against the PDF once it is in `docs/manuals/`.

## Connecting over TCP/IP

| Item | Value |
| --- | --- |
| Interface | Ethernet, "bare socket" (raw TCP) |
| Port | **5025** (telnet is on 5024; VXI-11 is also available) |
| Command terminator | LF (`\n`); CR LF is also accepted |
| Response terminator | CR LF (`\r\n`) |
| Character set | ASCII |
| Several commands per line | Separate with `;` |

The bare-socket, telnet and VXI-11 services, DHCP/Auto-IP/static addressing and the static address
itself are all configured with `IFCF` (below) or from the front panel.

## Syntax

* Commands are four-letter mnemonics (or IEEE-488.2 `*` commands), case-insensitive.
* `(?)` means the command also has a query form. `{ }` marks optional parameters (omitted in the query form).
* Parameters are separated by commas. Numbers may use exponents (`1e-6`).
* Queries return their value followed by CR LF. Set commands return nothing.
* Errors are not sent back automatically: read them with `LERR?` (0 = no error).
* Query parameters follow the `?` directly or after a space: `DLAY?2` and `DLAY? 2` are equivalent.

Parameter letters:

| Letter | Meaning |
| --- | --- |
| `c`, `d` | Delay channel (table below) |
| `b` | Output (table below) |
| `i`, `j` | Integer |
| `t` | Time in seconds |
| `f` | Frequency in hertz |
| `v` | Voltage in volts |

### Channels (`c`, `d`)

| Value | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Channel | T0 | T1 | A | B | C | D | E | F | G | H |

### Outputs (`b`)

| Value | 0 | 1 | 2 | 3 | 4 |
| --- | --- | --- | --- | --- | --- |
| Output | T0 | AB | CD | EF | GH |

Output AB is a pulse from channel A's time to channel B's time; likewise CD, EF and GH.

## Commands

### IEEE-488.2 common commands

| Command | Description |
| --- | --- |
| `*CAL?` | Run auto calibration. Returns 0 on success or an error code. |
| `*CLS` | Clear all status registers. |
| `*ESE(?) {i}` † | Standard event status enable register (bit mask). |
| `*ESR?` † | Standard event status register. Reading clears it. |
| `*IDN?` | Identification: `Stanford Research Systems,DG645,s/n######,ver#.##.##` |
| `*OPC(?)` † | Operation complete. The query returns 1 when all prior commands have finished. |
| `*PSC(?) {i}` † | Power-on status clear: 1 clears the enable registers at power on. |
| `*RCL i` | Recall settings from location *i* (0 = factory defaults, 1–9 = user locations). |
| `*RST` | Reset to default settings. |
| `*SAV i` | Save settings to location *i* (1–9). |
| `*SRE(?) {i}` † | Service request enable register (bit mask). |
| `*STB?` † | Serial poll status byte. |
| `*TRG` | Trigger a delay cycle when the trigger source is a single-shot mode. |
| `*TST?` | Run the self test. Returns 0 on success or an error code. |
| `*WAI` | Wait for all prior commands to finish. |

### Status and display

| Command | Description |
| --- | --- |
| `DISP(?) {i,j}` | Front-panel display: *i* = mode (table below), *j* = channel or output shown. `DISP?` returns `i,j`. |
| `INSE(?) {i}` † | Instrument status enable register. |
| `INSR?` | Instrument status register (bits below). Reading clears it. |
| `LERR?` | Last error from the error buffer; reading removes it. Read until 0 to empty the buffer. |
| `TIMB?` † | Timebase in use: 0 internal, 1 optional OCXO, 2 optional rubidium, 3 external. |

Display modes (`DISP i`): 0 trigger rate, 1 trigger threshold, 2 trigger single shot, 3 trigger line,
4 advanced triggering enable, 5 trigger holdoff, 6 prescale configuration, 7 burst mode, 8 burst delay,
9 burst count, 10 burst period, 11 channel delay, 12 channel output levels, 13 channel output polarity,
14 burst T0 configuration. For modes 12 and 13, *j* is an output (0–4); otherwise a channel (0–9).

### Trigger

| Command | Description |
| --- | --- |
| `TSRC(?) {i}` | Trigger source (table below). |
| `TRAT(?) {f}` | Internal trigger rate in Hz (100 µHz to 10 MHz). |
| `TLVL(?) {v}` | External trigger threshold in volts (±3.5 V). |
| `HOLD(?) {t}` | Trigger holdoff in seconds (needs advanced triggering). |
| `INHB(?) {i}` | Inhibit input action: 0 off, 1 triggers, 2 AB, 3 AB and CD, 4 AB, CD and EF, 5 AB, CD, EF and GH. |
| `ADVT(?) {i}` | Advanced triggering (holdoff and prescalers): 0 off, 1 on. |
| `PRES(?) b{,i}` | Prescale factor: *b* = 0 trigger input, 1 AB, 2 CD, 3 EF, 4 GH. |
| `PHAS(?) b{,i}` | Prescale phase of output *b* (1 AB, 2 CD, 3 EF, 4 GH), 0 to factor − 1. |

Trigger sources (`TSRC i`):

| Value | Source |
| --- | --- |
| 0 | Internal |
| 1 | External, rising edge |
| 2 | External, falling edge |
| 3 | Single-shot external, rising edge |
| 4 | Single-shot external, falling edge |
| 5 | Single shot |
| 6 | Line |

### Burst

| Command | Description |
| --- | --- |
| `BURM(?) {i}` | Burst mode: 0 off, 1 on. |
| `BURC(?) {i}` | Burst count: delay cycles per burst. |
| `BURD(?) {t}` | Burst delay: trigger to first cycle, in seconds. |
| `BURP(?) {t}` | Burst period: time between cycles, in seconds (from 100 ns). |
| `BURT(?) {i}` | Burst T0 configuration: 0 T0 on every cycle, 1 T0 on the first cycle only. |

### Delays and output levels

| Command | Description |
| --- | --- |
| `DLAY(?) c{,d,t}` | Set channel *c* to channel *d* plus *t* seconds. `DLAY?c` returns `d,t`, e.g. `0,+0.000001000000`. |
| `LINK(?) c{,d}` | Link channel *c* to reference channel *d*. |
| `SSDL(?) c{,t}` | Step size of channel *c*'s delay. |
| `SPDL c,i` | Step channel *c*'s delay down (*i* = 0) or up (*i* = 1) by its step size. |
| `LAMP(?) b{,v}` | Output *b* amplitude in volts. |
| `LOFF(?) b{,v}` | Output *b* offset in volts. |
| `LPOL(?) b{,i}` | Output *b* polarity: 0 negative, 1 positive. |
| `SSLA(?) b{,v}` | Step size of output *b*'s amplitude. |
| `SSLO(?) b{,v}` | Step size of output *b*'s offset. |
| `SPLA b,i` | Step output *b*'s amplitude down (0) or up (1). |
| `SPLO b,i` | Step output *b*'s offset down (0) or up (1). |

Common output presets (used by this program's TTL/NIM buttons): TTL is amplitude 4.0 V, offset 0 V,
positive polarity. NIM is amplitude 0.8 V, offset −0.8 V, negative polarity (0 V idle, −0.8 V pulse).

### Interface

| Command | Description |
| --- | --- |
| `IFCF(?) i{,j}` | Interface configuration (table below). Changes take effect after `IFRS` or a power cycle. |
| `IFRS i` | Reset interface *i*: 0 RS-232, 1 GPIB, 2 LAN. Resetting LAN drops TCP connections. |
| `EMAC?` | Ethernet MAC address. |
| `LCAL` | Go to local (unlock the front panel). |
| `REMT` | Go to remote (lock the front panel). |
| `LOCK?` † | Request the exclusive control lock. Returns 1 if granted. |
| `UNLK?` † | Release the lock. Returns 1 if this interface held it. |

`IFCF` parameters:

| *i* | Setting | Values |
| --- | --- | --- |
| 0 | RS-232 enable | 0 / 1 |
| 1 | RS-232 baud rate | 4800, 9600, 19200, 38400, 57600, 115200 |
| 2 | GPIB enable | 0 / 1 |
| 3 | GPIB address | 0–30 |
| 4 | LAN TCP/IP enable | 0 / 1 |
| 5 | DHCP enable | 0 / 1 |
| 6 | Auto-IP enable | 0 / 1 |
| 7 | Static IP enable | 0 / 1 |
| 8 | Bare socket (port 5025) enable | 0 / 1 |
| 9 | Telnet (port 5024) enable | 0 / 1 |
| 10 | VXI-11 net instrument enable | 0 / 1 |
| 11 | Static IP address | a.b.c.d |
| 12 | Subnet mask | a.b.c.d |
| 13 | Default gateway | a.b.c.d |

## Status registers

### Instrument status register (`INSR?` / `INSE`)

| Bit | Name | Meaning |
| --- | --- | --- |
| 0 | TRIG | Got a trigger. |
| 1 | RATE | Got a trigger while a delay or burst was in progress. |
| 2 | END_OF_DELAY | A delay cycle has completed. |
| 3 | END_OF_BURST | A burst cycle has completed. |
| 4 | INHIBIT | A trigger or output delay cycle was inhibited. |
| 5 | ABORT_DELAY | A delay cycle was aborted early. |
| 6 | PLL_UNLOCK | The 100 MHz PLL came unlocked. |
| 7 | RB_UNLOCK | The installed rubidium oscillator is unlocked. |

### Standard event status register (`*ESR?` / `*ESE`) †

| Bit | Name | Meaning |
| --- | --- | --- |
| 0 | OPC | Operation complete. |
| 2 | QYE | Query error. |
| 3 | DDE | Device dependent error. |
| 4 | EXE | Execution error. |
| 5 | CME | Command error. |
| 7 | PON | Power on. |

### Serial poll status byte (`*STB?` / `*SRE`) †

| Bit | Name | Meaning |
| --- | --- | --- |
| 0 | INSB | An enabled bit in the instrument status register is set. |
| 4 | MAV | Message available. |
| 5 | ESB | An enabled bit in the standard event status register is set. |
| 6 | MSS | Master summary status. |

## Error codes (`LERR?`)

| Code | Meaning | Code | Meaning |
| --- | --- | --- | --- |
| 0 | No error | 60 | Failed sample and hold operations test |
| 10 | Illegal value | 61 | Failed Vjitter operations test |
| 11 | Illegal mode | 62–71 | Failed channel T0, T1, A–H analog delay test |
| 12 | Illegal delay | 80 | Failed sample and hold calibration |
| 13 | Illegal link | 81–90 | Failed T0, T1, A–H calibration |
| 14 | Recall failed | 91 | Failed Vjitter calibration |
| 15 | Not allowed | 110 | Illegal command |
| 16 | Failed self test | 111 | Undefined command |
| 17 | Failed auto calibration | 112 | Illegal query |
| 30 | Lost data | 113 | Illegal set |
| 32 | No listener | 114 | Null parameter |
| 40 | Failed ROM check | 115 | Extra parameters |
| 41–45 | Failed offset T0, AB, CD, EF, GH test | 116 | Missing parameters |
| 46–50 | Failed amplitude T0, AB, CD, EF, GH test | 117 | Parameter overflow |
| 51 | Failed FPGA communications test | 118 | Invalid floating point number |
| 52 | Failed GPIB communications test | 120 | Invalid integer |
| 53 | Failed DDS communications test | 121 | Integer overflow |
| 54 | Failed serial EEPROM communications test | 122 | Invalid hexadecimal |
| 55 | Failed temperature sensor communications test | 126 | Syntax error |
| 56 | Failed PLL communications test | 170 | Communication error |
| 57–59 | Failed DAC 0, 1, 2 communications test | 171 | Over run |
| | | 254 | Too many errors |

## Examples

```text
*IDN?                     → Stanford Research Systems,DG645,s/n001234,ver1.14.10E
TSRC 1                    external trigger, rising edge
TLVL 1.5                  1.5 V threshold
DLAY 2,0,1e-6             A = T0 + 1 µs
DLAY 3,2,250e-9           B = A + 250 ns   (AB is a 250 ns pulse starting at 1 µs)
LAMP 1,4.0;LOFF 1,0       AB output: 4 V TTL-style pulse
DLAY?3                    → 2,+0.000000250000
BURM 1;BURC 5;BURP 1e-6   burst of 5 cycles, 1 µs apart, per trigger
LERR?                     → 0
```

## Where each command appears in Strobe

| Page | Commands |
| --- | --- |
| Delays | `DLAY`, `SSDL`, `SPDL` (references are set through `DLAY`) |
| Outputs | `LAMP`, `LOFF`, `LPOL`, `SSLA`, `SSLO`, `SPLA`, `SPLO` |
| Trigger | `TSRC`, `TRAT`, `TLVL`, `INHB`, `ADVT`, `HOLD`, `PRES`, `PHAS`, `*TRG` |
| Burst | `BURM`, `BURC`, `BURP`, `BURD`, `BURT` |
| Setups | `*SAV`, `*RCL`, `*RST`; saved setups in the configuration and `.dg645.json` setup files (every delay, output, trigger and burst command) |
| Status | `INSR?`, `INSE`, `*ESR?`, `*ESE`, `*STB?`, `*SRE`, `*PSC`, `LERR?`, `*CLS`, `*TST?`, `*CAL?`, `TIMB?`, `DISP`, `*IDN?` |
| Interfaces | `IFCF` (all 14 settings), `IFRS`, `EMAC?`, `LCAL`, `REMT`, `LOCK?`, `UNLK?` |
| Terminal | Anything, typed by hand (including `LINK`, `*OPC?` and `*WAI`) |
