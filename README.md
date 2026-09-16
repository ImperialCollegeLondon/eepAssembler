# eepAssembler

An assembler for the EEP1 CPU, as taught at Imperial College. It turns EEP1 assembly
language into a `.ram` file of machine code that can be loaded into Issie.

*This code is written by Tom Clarke with no guarantee as to its correctness: please e-mail me if you find errors.*

Use issues on this repo for feature requests. The F# source is in `./src/Program.fs`.

## Quick start

**1. Install the .NET SDK**, if you do not already have it:
[dotnet.microsoft.com/download](https://dotnet.microsoft.com/en-us/download). Take the
**latest** version offered, and make sure it is the **SDK** and not the "runtime only"
download.

Any SDK from 8 upwards can build the assembler, so if you already have 8, 9, 10 or later
there is nothing to do. Do note that .NET 8 and .NET 9 both stopped receiving security
updates on 10 November 2026, so if you are installing anything at all, install the
current one. Installing a new .NET never removes the versions you already have.

**Pick the download that matches your machine**: `x64` for almost every Windows PC and
for Intel Macs, `Arm64` for Apple Silicon Macs and for Arm Windows laptops (Snapdragon,
Copilot+ PCs). On Windows, if you are not sure, take `x64` - it also runs on Arm Windows,
just more slowly. Avoid the `x86` (32 bit) download: it works, but having both it and an
`x64` install is the single most common cause of the installation problems below.

**2. Get the code**: download and unzip the latest release, or fork and clone this repo.

**3. Open a terminal in this directory** - the one containing this README file - and run:

```
dotnet run
```

That is the whole installation. The first run takes a few seconds to compile.

You will see your computer's own **"choose a folder" dialog** - the normal Windows,
macOS or Linux one - asking which directory to watch. Pick the directory holding your
assembly files and every `.s` or `.txt` file in it will be assembled.

On a machine with no such dialog (Linux without `zenity` or `kdialog`, or a terminal
with no screen to put a window on) a **console chooser** is used instead, which does
the same job by typing:

```
C:\GitHub\eepAssembler>dotnet run
EEP1 Assembler: Version 2.4

==================================================================
 Choose a directory to watch. Every .s or .txt
 file in the directory you choose will be assembled now, and
 re-assembled every time you save it. Subdirectories are not watched.
==================================================================

Directory: C:\GitHub\eepAssembler

  Files here that would be watched (2):
      assem.txt
      testpipeline.txt

  Subdirectories:
     1  bin
     2  obj
     3  src

  ENTER   watch this directory
  1..3    go into that subdirectory
  ..      go up to the parent directory
  <path>  go to that directory
  q       quit
>
```

It lists the assembly files in the directory you are looking at - those are exactly the
files that will be watched. Move around with the numbers, `..`, or by typing a path,
until the directory holding your assembly files is shown, then press ENTER:

```
Watching C:\GitHub\eepAssembler
Every .s or .txt file in it is assembled now, and again whenever it is saved.

assem.txt -> assem.ram: 9 lines
```

The directory is named in full once, here. Everything reported after that is a file
inside it, so only file names are used, and each file's report is followed by a blank
line so one re-assembly is easy to tell from the next.

The assembler now stays running. **Edit an assembly file and save it, and it is
re-assembled immediately** - leave this window open beside your editor. Press Ctrl-C
in the terminal to stop.

The repo contains `assem.txt` as a worked example, which assembles to `assem.ram`.

### Check it worked

`assem.ram` should look like this - each line is an address, the machine code word at
that address, and the source line that produced it:

```
0x00 0x0101   // MOV R0, #1
0x01 0x0302   // MOV R1, #2
0x02 0x1244   // ADD R1, R1, R2
...
```

### Other ways to start it

* `dotnet run -- dir` skips the chooser and watches `dir` straight away. `dir` may also
  be a file, in which case the directory containing it is watched.
* `dotnet run -- -c` opens the chooser explicitly.
* `dotnet run -- -ct` opens the console chooser, skipping the folder dialog. Use this
  if the dialog misbehaves, or over a remote connection with no screen.
* `dotnet run -- -i` runs the assembler interactively: type one line of assembly at a
  time and its machine code word is printed. Type `q` to quit.
* On Windows only, double-click `chooser.bat` to pick a *file* rather than a directory
  with the Windows dialog: the directory containing it is then watched.

## Writing assembly for it

* Source files have extension `.s` or `.txt`. Both work identically, so files written
  for earlier versions of this assembler still work.
   * `prog.s` and `prog.txt` in the same directory would both write `prog.ram`, so the
     assembler warns if it finds such a pair.
* Only the chosen directory is watched - files in its subdirectories are ignored.
* `//` starts a comment, which runs to the end of the line.
* A line may be labelled. The label may be written with or without a colon:
  `loop: MOV R0, #1`, `loop : MOV R0, #1` and `loop MOV R0, #1` all mean the same thing.
* Labels can be used as the Imm8 operand of jump and memory instructions.

### Imm8 operands

Imm8 operands may be written anywhere in the range -128 to 255.

* `128` to `255` and `-128` to `-1` are the same 8 bit patterns, so `#255` and `#-1`
  assemble to the same machine code. A value written as `128` to `255` is read back by
  the CPU as the corresponding negative number, so the assembler prints a **warning**
  saying so, and still writes the machine code.
* An operand outside -128 to 255 - including a label whose address is above 255 - is an
  **error**. Use an `EXT` instruction to supply the high byte of a larger operand:

```
EXT 4            // high byte of the address used by the next instruction
LDR R0, [0x00]   // reads memory location 0x0400
```

* `EXT` supplies the high byte itself, so no sign warning is given for the instruction
  it modifies.

### The generated `.ram` file

Each line is annotated with the source line that produced it, as a `//` comment: the
label if there is one, then the mnemonic and operands. Comments in your assembly source
are *not* copied across.

```
0x00 0x0101   // start: MOV R0, #1
0x01 0x1101   // loop: ADD R0, #1
0x02 0xc0ff   // JMP loop
```

### Errors and warnings

Errors and warnings are printed in the terminal, with the line number they came from.
They are never written into your assembly file, which the assembler only ever reads.

An error stops assembly, and no `.ram` file is written - the one from the last good
assembly, if there is one, is left as it was:

```
prog.s: 2 errors, prog.ram not written
  line 4: immediate 300 is outside -128 .. 255 - use EXT for the high byte
  line 9: duplicate label 'loop'
```

Warnings do not stop assembly. The `.ram` file is written and the warnings follow it:

```
prog.s -> prog.ram: 12 lines, 1 warning
  line 7: immediate 200 sets the top bit, so the CPU reads it as -56
```

## Troubleshooting

If `dotnet run` does not work, check what you have installed:

* Open a terminal (Windows key-r -> cmd, or equivalent on other systems)
* Run `dotnet --info`

You should see an SDK version of 8 or higher, and a 64 bit RID - `win-x64`, or `win-arm64`
on an Arm laptop, or `osx-arm64` / `linux-x64` and so on:

```
C:\Users\tomcl>dotnet --info
.NET SDK:
 Version:   10.0.302

Runtime Environment:
 OS Name:     Windows
 OS Platform: Windows
 RID:         win-x64
 Base Path:   C:\Program Files\dotnet\sdk\10.0.302\
```

What can go wrong:

* **`dotnet` is not a recognised command** - the SDK is not installed, or its directory
  is not on your PATH. Re-run the installer and open a new terminal afterwards.
* **You installed the runtime, not the SDK** - the download page offers both. `dotnet --info`
  lists an SDK version only if you have the SDK.
* **Your SDK is older than 8** - the build stops with `NETSDK1045: The current .NET SDK does
  not support targeting .NET 8.0`. Install a current SDK from the link above, then open a
  new terminal. Installing a new .NET does not remove the old ones.
* **A 32 bit install is found first** - if `dotnet --info` shows a RID of `win-x86` on a 64
  bit machine then a 32 bit `dotnet` is earlier on your PATH than the 64 bit one, so the SDK
  you installed is not the one being used. The build prints a warning saying so. Fix it by
  putting `C:\Program Files\dotnet` ahead of `C:\Program Files (x86)\dotnet` in your PATH
  (Windows key, then search for "environment variables") and opening a new terminal.
  Uninstalling the 32 bit .NET works just as well.
* **Which .NET am I actually using?** - the assembler prints it on its first line, e.g.
  `EEP1 Assembler: Version 2.4 (running on .NET 10.0.2, x64)`. If you e-mail about a
  problem, include that line.
* **`I cannot find a directory ...` in the chooser** - type an absolute path, or use the
  numbered subdirectories to navigate there instead.

## To develop

On Windows:

* Install *Visual Studio 2026* with F# desktop
* * load `./eepassem.sln`

The project targets `net8.0` with `RollForward` set to `LatestMajor`. That will build with whatever .NET you already have.
`net8.0` is a *floor* - the roll-forward means the program runs on the newest runtime installed,
so on a machine with only .NET 11 it runs on .NET 11.

.NET 8 and .NET 9 left support on 10 November 2026, which changes none of this, because
nothing here runs on .NET 8. It does mean SDKs released after that date warn (NETSDK1138)
that the target framework is out of support, so `CheckEolTargetFramework` is set to
`false` to keep that warning off every screen.

