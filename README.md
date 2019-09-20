# Bonsai.TeledyneDALSA
[Bonsai](http://www.open-ephys.org/bonsai/) library containing modules for acquiring images from Teledyne DALSA cameras that use the [Sapera LT SDK](http://www.teledynedalsa.com/imaging/products/software/sapera/).

## Installation
The NuGet package for this library can be found in Bonsai's package manager. It is available at [MyGet](https://www.myget.org/feed/bonsai-community/package/nuget/Bonsai.TeledyneDALSA).

Additionally, a NuGet package can be generated from source by running:

```batchfile
.nuget\NuGet.exe pack Bonsai.TeledyneDALSA\Bonsai.TeledyneDALSA.csproj
```

## Usage
Oen up the Bonsai Package Manager, go to settings, and then add the .nuget folder path found in the Bonsai.TeledyneDALSA repository to the list of available package sources. After the NuGet package is added to Bonsai, a **SaperaCapture** source node should become available. Adding this node to your workflows will allow you to produce a sequence of frames captured from a connected Teledyne DALSA Gig-E camera that uses the Sapera LT SDK.

### Notes:
Running the **SaperaCapture** node requires Bonsai to be launched in no boot mode, with the additional --noboot flag, otherwise an error will be thrown. To launch bonsai in no boot mode from the command line, use:

`Bonsai.exe --noboot`

This package was originally developed and tested with the Teledyne DALSA Genie Nano Gig-E camera series. Since I have not tested other Teledyne DALSA cameras, I cannot ensure that this package will work with your board and camera setup. However, I encourage you to try it out anyways and let me know if it works.
