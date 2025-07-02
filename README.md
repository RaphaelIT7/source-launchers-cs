# srcds-cs

This is an experiment to replicate ``dedicated_main`` in C#. It performs basically the same task that srcds.exe would do, since srcds.exe is just loading the dedicated.dll module and calling the ``DedicatedMain`` procedure.

This needs to be opened in the same directory srcds.exe would be in, with the necessary dll's/exe's/etc. 

I just have this in my Directory.Build.props:

```
<Project>
  <PropertyGroup>
    <OutputPath>C:\your\server\path\here</OutputPath>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>
  </PropertyGroup>
</Project>
```

You can then run with debugging and see it work.

I have some detouring stuff because the purpose of this originally was to add functionality to the ``clear`` concommand on a dedicated server. The detouring is thanks to MinHook.NET with some changes I made to support Native AOT building a while back (although that isn't necessary for this project).