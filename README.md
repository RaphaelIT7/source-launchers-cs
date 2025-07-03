# srcds-cs

This is an experiment to replicate ``launcher_main`` and ``dedicated_main`` in C#. It performs basically the same task that hl2.exe/srcds.exe do, since those executables just load the launcher/dedicated.dll modules and call the ``LauncherMain``/``DedicatedMain`` procedures respectively.

## Launcher

UserSpecific.props:
```
<Project>
	<PropertyGroup Condition="'$(Platform)' == 'x86'">
		<OutputPath>path\to\GarrysMod\bin</OutputPath>
	</PropertyGroup>

	<PropertyGroup Condition="'$(Platform)' == 'x64'">
		<OutputPath>path\to\GarrysMod\bin\win64</OutputPath>
	</PropertyGroup>

	<PropertyGroup>
		<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
		<AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
	</PropertyGroup>
</Project>
```

launchSettings.json:
```
{
  "profiles": {
    "launcher-cs": {
      "commandName": "Project",
      "nativeDebugging": true
    }
  }
}
```

## SRCDS

UserSpecific.props:
```
<Project>
  <PropertyGroup>
    <OutputPath>C:\your\server\path\here</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
  </PropertyGroup>
</Project>
```

launchSettings.json:
```
{
  "profiles": {
    "srcds-cs": {
      "commandName": "Project",
      "commandLineArgs": "-console +maxplayers 4 +gamemode sandbox +map gm_flatgrass +hostname \"My Cool Server\" -tickrate 66 -noworkshop -noaddons"
    }
  }
}
```

You can then run with debugging and see it work.

I have some detouring stuff because the purpose of this originally was to add functionality to the ``clear`` concommand on a dedicated server. The detouring is thanks to MinHook.NET with some changes I made to support Native AOT building a while back (although that isn't necessary for this project).
