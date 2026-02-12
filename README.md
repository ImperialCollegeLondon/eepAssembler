# eepAssembler

A simple assembler for EEP1 CPU as taught at Imperial College 2023, 2024

*This code is written by Tom Clarke with no guarantee as to its correctness: please e-mail me if you find errors*

Use issues on this repo for feature requests

The F# source is in `./src/Program.fs`

## To run the assembler on any system (Windows/Macos/Linux)

Before you start:

* Download and unzip the latest release source code or fork and clone this repo.
* Install [.NET 64 bit SDK 8](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) from link that fits your laptop and operating system.

To run the assembler on any system (Windows users see **simpler and nicer option for Windows** below):

* start a command line terminal running in this directory (the one containing the downloaded README file).
* `dotnet run <dir>`
   * replace `<dir>` by the directory you want to watch
   * To test the the assembler just `dotnet run` and it will watch the assembler
* `dotnet run` will watch `.`
    * The downloaded `.` directory has a file `assem.txt` which will be correctly assembled into `assem.ram` with a success message as below.
    
```
C:\GitHub\eepAssembler>dotnet run
Watching '.'
Successful assembly of '.\assem.txt'
7 lines written to '.\assem.ram'   
```


### On Windows systems only

* Double-click `chooser.bat` from this directory to use file selection GUI
* Select a file to assemble (e.g. `assem.txt`)
* The whole directory of file selected will be watched.

## Features

* The assembler will watch a directory and turn any `.txt` file of EEP1 assembly language into a `.ram` file of machine code suitable for use by Issie.
* If assembly errors exist they will be printed out
* Files that change will get re-assembled, so you can edit a file and save it with auto-assembly on save.
* Lines can be labelled (see `assem.txt`) and labels used in jump or memory instructions as Imm8 operands.

## Troubleshooting your installation

Uptodate as of Feb 2023.

If this program does not run it will likely be because you have the wrong version of .Net installed. You need 64 bit .Net 8 SDK. Check you have this as follows:

* Run a command prompt (Windows key-r -> cmd, or equiv on other systems)
* `dotnet --info`

You should get something like:

```
C:\Users\tomcl>dotnet --info
.NET SDK:
 Version:   8.0.101
 Commit:    bb24aafa11

Runtime Environment:
 OS Name:     Windows
 OS Version:  10.0.19044
 OS Platform: Windows
 RID:         win10-x64
 Base Path:   C:\Program Files\dotnet\sdk\8.0.101\
```
The important bits are: .NET SDK, Version: 8, RID xxxx-x64

What can go wrong: 

* Even though you have installed 64 bit dotnet SDK, you have a previous 32 bit install that was done earlier and dotnet on a command line always finds that one - change your path
* You have dotnet 8 installed - but not the SDK
* You have dotnet 6 or 7 installed, but not dotnet 8

### For more insight

* Run a command prompt in the eepassembler directory (the one containing chooser.bat).
* Run `dotnet run`.
* Check the messages there, e.g. which version, is there an error.


## To develop

On windows:
* Install *Visual Studio 2022* with F# desktop
* (if needed) install [.NET SDK](https://dotnet.microsoft.com/en-us/download/visual-studio-sdks) 
* load `./eepassem.sln`

See [HLP setup](https://intranet.ee.ic.ac.uk/t.clarke/hlp/install-notes.html) for more details of different dev environments.


