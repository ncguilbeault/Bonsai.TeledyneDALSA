# Bonsai.TeledyneDALSA
[Bonsai](http://www.open-ephys.org/bonsai/) library containing modules for acquiring images from Teledyne DALSA cameras that use the [Sapera LT SDK](http://www.teledynedalsa.com/imaging/products/software/sapera/).

## Installation
The NuGet package for this library can be found in Bonsai's package manager. It is available at [MyGet](https://www.myget.org/feed/bonsai-community/package/nuget/Bonsai.TeledyneDALSA).

Additionally, a NuGet package can be generated from source by running:

```batchfile
.nuget\NuGet.exe pack Bonsai.TeledyneDALSA\Bonsai.TeledyneDALSA.csproj
```

## Usage
After the NuGet package is added to Bonsai, a **SaperaCapture** source node will become available. This node will produce a sequence of frames captured from a connected Teledyne DALSA camera that uses the Sapera LT SDK.

### Notes:
Running the **SaperaCapture** node requires Bonsai to be launched in no boot mode, with the additional --noboot flag. From the command line, use:

`Bonsai.exe --noboot`

This has only been tested with the Teledyne DALSA Genie Nano Camera Series. 
