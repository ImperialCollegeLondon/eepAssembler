# eepAssembler

An assembler for the EEP1 CPU, as taught at Imperial College. It turns EEP1 assembly
language into a `.ram` file of machine code that can be loaded into Issie.

*This code is written by Tom Clarke with no guarantee as to its correctness: please e-mail me if you find errors.*

Use issues on this repo for feature requests. The F# source is in `./src/Program.fs`.

## Quick start

**1. Install the .NET SDK**, if you do not already have it: [dotnet.microsoft.com/download](https://dotnet.microsoft.com/en-us/download).
Any version from 8 upwards will do - 8, 9, 10 and later all work, so if you already
have one of these installed there is nothing to do. Get the 64 bit **SDK**, not the
"runtime only" download.

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
Watching 'C:\GitHub\eepAssembler' for .s or .txt files
Successful assembly of 'C:\GitHub\eepAssembler\assem.txt'
9 lines written to 'C:\GitHub\eepAssembler\assem.ram'
```

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

Assembly errors are printed with their line number and nothing is written:

```
Assembly errors in file 'prog.s':
Line no 4: Immediate operand 300 is outside the allowed 8 bit range -128 .. 255. Use an EXT instruction before this one to supply the high byte of a larger operand
```

Warnings do not stop assembly - the `.ram` file is written and the warnings follow it.

## Troubleshooting

If `dotnet run` does not work, check what you have installed:

* Open a terminal (Windows key-r -> cmd, or equivalent on other systems)
* Run `dotnet --info`

You should see an SDK version of 8 or higher, and a 64 bit RID such as `win-x64`:

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
* **Your SDK is older than 8** - install a current one from the link above. Installing a
  new .NET does not remove the old ones.
* **An old 32 bit install is found first** - if `dotnet --info` shows a RID like `win-x86`,
  a 32 bit install is earlier on your PATH than the 64 bit one. Fix your PATH.
* **`I cannot find a directory ...` in the chooser** - type an absolute path, or use the
  numbered subdirectories to navigate there instead.

## To develop

On Windows:

* Install *Visual Studio 2022* with F# desktop
* (if needed) install the [.NET SDK](https://dotnet.microsoft.com/en-us/download/visual-studio-sdks)
* load `./eepassem.sln`

The project targets `net8.0` with `RollForward` set to `LatestMajor`. That combination is
deliberate: the oldest supported target framework means every current SDK can build it,
and the roll-forward means the built program runs on whatever newer .NET runtime a
machine happens to have. Raising the target framework would stop anyone with an older
SDK from building the project at all, so leave it alone unless you also want to require
everyone to upgrade.

See [HLP setup](https://intranet.ee.ic.ac.uk/t.clarke/hlp/install-notes.html) for more
details of different dev environments.
