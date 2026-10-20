# Third-party notices

Devinno.Communication itself is MIT-licensed (see `LICENSE`). It does not contain or embed third-party source code. This file lists the third-party components it depends on and the outside sources that some tests were checked against.

## Package dependencies (referenced, not redistributed)

These are ordinary NuGet dependencies. NuGet downloads them separately; their files are not inside the Devinno.Communication package.

| Package | License | Project |
|---|---|---|
| MQTTnet | MIT | https://github.com/dotnet/MQTTnet |
| System.IO.Ports | MIT | https://github.com/dotnet/runtime |

## Test reference data (tests only, not in the package)

Some unit tests compare frames against short byte sequences taken from public protocol captures and documentation. These are a few dozen bytes each and are used only as expected values in `tests/`. Sources:

- Omron FINS: sample captures from the Cybersecurity and Infrastructure Security Agency's `icsnpp-omron-fins` project (BSD 3-Clause License) — https://github.com/cisagov/icsnpp-omron-fins
- HSMS / SECS-II: the `sample_hsms.pcapng` capture attached to Wireshark issue 12942 — https://gitlab.com/wireshark/wireshark/-/issues/12942
- Siemens S7: byte layouts confirmed against public captures and the behaviour of Snap7, libnodave and S7.Net (no code from these projects is included).
- Vendor protocol manuals (Mitsubishi, Omron, LS ELECTRIC): worked examples from the manuals, cited by section in the test comments.

## Optional test tool (not included)

- secsgem (LGPL-2.1, https://github.com/bparzella/secsgem): some SECS/GEM interoperability tests start it as a separate Python process when it is installed. It is not part of this repository or the package; without it those tests are skipped.
