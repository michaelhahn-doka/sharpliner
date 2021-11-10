﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Build.Framework;

namespace Sharpliner;

/// <summary>
/// This is an MSBuild task that is run in user projects to publish YAMLs after build.
/// </summary>
public class PublishDefinitions : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// Assembly that will be scaned for pipeline definitions.
    /// </summary>
    [Required]
    public string? Assembly { get; set; }

    /// <summary>
    /// You can make the task fail in case it finds a YAML whose definition changed.
    /// This is for example used in the ValidateYamlsArePublished build step that checks that YAML changes were checked in.
    /// </summary>
    public bool FailIfChanged { get; set; }

    public override bool Execute() => PublishAllDefinitions<DefinitionBase>();

    /// <summary>
    /// This method finds all pipeline definitions via reflection and publishes them to YAML.
    /// </summary>
    /// <typeparam name="T">Base type of the definitions we're looking for</typeparam>
    private bool PublishAllDefinitions<T>() where T : DefinitionBase
    {
        var pipelines = FindAllImplementations<T>();

        foreach (var pipeline in pipelines)
        {
            PublishDefinition(pipeline);
        }

        return pipelines.Count > 0;
    }

    /// <summary>
    /// Loads the assembly and all of its dependencies (such as YamlDotNet).
    /// </summary>
    private Assembly LoadAssembly(string assemblyPath)
    {
        // Preload dependencies needed for things to work
        var assemblies = new[] { "YamlDotNet.dll", "Sharpliner.dll" }
            .Select(assemblyName => Path.Combine(Path.GetDirectoryName(assemblyPath) ?? throw new Exception($"Failed to find directory of {assemblyPath}"), assemblyName))
            .Select(Path.GetFullPath)
            .Select(path => System.Reflection.Assembly.LoadFile(path) ?? throw new Exception($"Failed to find a Sharpliner dependency at {path}. Make sure the bin/ directory of your project contains this library."))
            .Where(a => a is not null)
            .ToDictionary(a => a.FullName!);

        Assembly ResolveAssembly(object? sender, ResolveEventArgs e)
        {
            if (!assemblies.TryGetValue(e.Name, out var res))
            {
                throw new Exception("Failed to find Sharpliner dependency " + e.Name);
            }

            return res;
        }

        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += ResolveAssembly;
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        // Load the final assembly where pipeline is defined
        return System.Reflection.Assembly.LoadFile(Path.GetFullPath(assemblyPath));
    }

    private void PublishDefinition(object definition)
    {
        Type type = definition.GetType();
        var getPath = type.GetMethod(nameof(DefinitionBase.GetTargetPath));
        var validate = type.GetMethod(nameof(DefinitionBase.Validate));
        var publish = type.GetMethod(nameof(DefinitionBase.Publish));

        if (publish is null || validate is null || getPath is null)
        {
            Log.LogError($"Failed to get pipeline definition metadata for {type.FullName}");
            return;
        }

        if (getPath.Invoke(definition, null) is not string path)
        {
            Log.LogError($"Failed to get target path for {type.Name} ");
            return;
        }

        Log.LogMessage(MessageImportance.High, $"{type.Name}:");
        Log.LogMessage(MessageImportance.High, $"  Validating pipeline..");

        try
        {
            validate.Invoke(definition, null);
        }
        catch (TargetInvocationException e)
        {
            Log.LogError("Validation of pipeline {0} failed: {1}{2}{3}",
                type.Name,
                e.InnerException?.Message ?? e.ToString(),
                Environment.NewLine,
                "To see exception details, build with more verbosity (dotnet build -v:n)");
            Log.LogMessage(MessageImportance.Normal, "Validation of pipeline {0} failed: {1}", type.Name, e.InnerException);
            return;
        }

        string? hash = GetFileHash(path);

        // Publish pipeline
        publish.Invoke(definition, null);

        if (hash == null)
        {
            if (FailIfChanged)
            {
                Log.LogError($"  This pipeline hasn't been published yet!");
            }
            else
            {
                Log.LogMessage(MessageImportance.High, $"  {type.Name} created at {path}");
            }
        }
        else
        {
            var newHash = GetFileHash(path);
            if (hash == newHash)
            {
                Log.LogMessage(MessageImportance.High, $"  No new changes to publish");
            }
            else
            {
                if (FailIfChanged)
                {
                    Log.LogError($"  Changes detected between {type.Name} and {path}!");
                }
                else
                {
                    Log.LogMessage(MessageImportance.High, $"  Published new changes to {path}");
                }
            }
        }
    }

    private List<object> FindAllImplementations<T>()
    {
        var assembly = LoadAssembly(Assembly ?? throw new ArgumentNullException(nameof(Assembly), "Assembly parameter not set"));

        var pipelines = new List<object>();
        var pipelineBaseType = typeof(T);

        foreach (Type type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
        {
            bool isPipelineDefinition = false;
            var baseType = type.BaseType;

            // I am unable to cast this to PipelineDefinitionBase and just do t.IsSubClass or t.IsAssignableTo because the types don't seem
            // to be the same even when they are (they come from the same code, but maybe different .dll files)..
            // I tried to make sure there is only one Sharpliner.dll but still couldn't get it to work so we have to parse invoke Publish via reflection
            while (baseType is not null)
            {
                isPipelineDefinition |= baseType.FullName == typeof(T).FullName && baseType.GUID == typeof(T).GUID;
                baseType = baseType.BaseType;
            }

            if (!isPipelineDefinition)
            {
                continue;
            }

            object? pipelineDefinition = Activator.CreateInstance(type);
            if (pipelineDefinition is null)
            {
                throw new Exception($"Failed to instantiate {type.GetType().FullName}");
            }

            pipelines.Add(pipelineDefinition);
        }

        return pipelines;
    }

    private static string? GetFileHash(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var md5 = MD5.Create();
        using var stream = File.OpenRead(path);
        return System.Text.Encoding.UTF8.GetString(md5.ComputeHash(stream));
    }
}