﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using ShaderGen.Tests.Attributes;
using ShaderGen.Tests.Tools;
using TestShaders;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;
using Xunit;
using Xunit.Abstractions;

namespace ShaderGen.Tests
{
    public class ShaderBuiltinsTests
    {
        /// <summary>
        /// How close float's need to be, to be considered a match.
        /// </summary>
        private const float Tolerance = 0.001f;// float.Epsilon * 2;

        /// <summary>
        /// The maximum test duration for each backend.
        /// </summary>
        private static readonly TimeSpan TestDuration = TimeSpan.FromSeconds(3);

        /// <summary>
        /// The maximum iteration for each backend.
        /// </summary>
        private static readonly int TestLoops = 10000;

        private readonly ITestOutputHelper _output;

        public ShaderBuiltinsTests(ITestOutputHelper output)
        {
            _output = output;
        }


        [GlslEs300Fact]
        public void TestShaderBuiltins_GlslEs300()
            => TestShaderBuiltins(ToolChain.GlslEs300);

        [Glsl330Fact]
        public void TestShaderBuiltins_Glsl330()
            => TestShaderBuiltins(ToolChain.Glsl330);

        [Glsl450Fact]
        public void TestShaderBuiltins_Glsl450()
            => TestShaderBuiltins(ToolChain.Glsl450);

        [HlslFact]
        public void TestShaderBuiltins_Hlsl()
            => TestShaderBuiltins(ToolChain.Hlsl);

        [MetalFact]
        public void TestShaderBuiltins_Metal()
            => TestShaderBuiltins(ToolChain.Metal);

        private void TestShaderBuiltins(ToolChain toolChain)
        {
            // Calculate when to finish.
            long startTicks = Stopwatch.GetTimestamp();

            string csFunctionName =
                $"{nameof(TestShaders)}.{nameof(ShaderBuiltinsComputeTest)}.{nameof(ShaderBuiltinsComputeTest.CS)}";
            Compilation compilation = TestUtil.GetTestProjectCompilation();

            LanguageBackend backend = toolChain.CreateBackend(compilation);

            /*
             * Compile backend
             */
            ShaderSetProcessor processor = new ShaderSetProcessor();

            ShaderGenerator sg = new ShaderGenerator(
                compilation,
                backend,
                null,
                null,
                csFunctionName,
                processor);

            ShaderGenerationResult generationResult = sg.GenerateShaders();
            GeneratedShaderSet set = generationResult.GetOutput(backend).Single();
            _output.WriteLine($"Generated shader set for {toolChain.Name} backend.");

            ToolResult compilationResult =
                toolChain.Compile(set.ComputeShaderCode, Stage.Compute, set.ComputeFunction.Name);
            if (compilationResult.HasError)
            {
                _output.WriteLine($"Failed to compile Compute Shader from set \"{set.Name}\"!");
                _output.WriteLine(compilationResult.ToString());
                Assert.True(false);
            }
            else
                _output.WriteLine($"Compiled Compute Shader from set \"{set.Name}\"!");

            Assert.NotNull(compilationResult.CompiledOutput);

            int sizeOfParametersStruct = Unsafe.SizeOf<ComputeShaderParameters>();

            // Create failure data structure, first by method #, then by field name.
            Dictionary<int, List<Tuple<ComputeShaderParameters, ComputeShaderParameters,
                IReadOnlyCollection<Tuple<string, object, object>>>>> failures
                = new Dictionary<int, List<Tuple<ComputeShaderParameters, ComputeShaderParameters,
                    IReadOnlyCollection<Tuple<string, object, object>>>>>();

            // We need two copies, one for the CPU & one for GPU
            ComputeShaderParameters[] cpuParameters = new ComputeShaderParameters[ShaderBuiltinsComputeTest.Methods];
            ComputeShaderParameters[] gpuParameters = new ComputeShaderParameters[ShaderBuiltinsComputeTest.Methods];
            int loops = 0;
            long durationTicks = 0;

            ShaderBuiltinsComputeTest cpuTest = new ShaderBuiltinsComputeTest();
            /*
             * Run shader on GPU.
             */
            using (GraphicsDevice graphicsDevice = toolChain.CreateHeadless())
            {
                ResourceFactory factory = graphicsDevice.ResourceFactory;
                using (DeviceBuffer inOutBuffer = factory.CreateBuffer(
                    new BufferDescription(
                        (uint)sizeOfParametersStruct * ShaderBuiltinsComputeTest.Methods,
                        BufferUsage.StructuredBufferReadWrite,
                        (uint)sizeOfParametersStruct)))

                using (Shader computeShader = factory.CreateShader(
                    new ShaderDescription(
                        ShaderStages.Compute,
                        compilationResult.CompiledOutput,
                        nameof(ShaderBuiltinsComputeTest.CS))))

                using (ResourceLayout inOutStorageLayout = factory.CreateResourceLayout(new ResourceLayoutDescription(
                    new ResourceLayoutElementDescription("InOutBuffer", ResourceKind.StructuredBufferReadWrite,
                        ShaderStages.Compute))))

                using (Pipeline computePipeline = factory.CreateComputePipeline(new ComputePipelineDescription(
                    computeShader,
                    new[] { inOutStorageLayout },
                    1, 1, 1)))


                using (ResourceSet computeResourceSet = factory.CreateResourceSet(
                    new ResourceSetDescription(inOutStorageLayout, inOutBuffer)))

                using (CommandList commandList = factory.CreateCommandList())
                {
                    // Ensure the headless graphics device is the backend we expect.
                    Assert.Equal(toolChain.GraphicsBackend, graphicsDevice.BackendType);

                    _output.WriteLine("Created compute pipeline.");

                    do
                    {
                        /*
                         * Build test data in parallel
                         */
                        Parallel.For(
                            0,
                            ShaderBuiltinsComputeTest.Methods,
                            i => cpuParameters[i] = gpuParameters[i] = GetRandom<ComputeShaderParameters>());

                        /*
                         * Run shader on CPU in parallel
                         */
                        cpuTest.InOutParameters = new RWStructuredBuffer<ComputeShaderParameters>(ref cpuParameters);
                        Parallel.For(0, ShaderBuiltinsComputeTest.Methods,
                            i => cpuTest.DoCS(new UInt3 { X = (uint)i, Y = 0, Z = 0 }));

                        // Update parameter buffer
                        graphicsDevice.UpdateBuffer(inOutBuffer, 0, gpuParameters);
                        graphicsDevice.WaitForIdle();

                        // Execute compute shaders
                        commandList.Begin();
                        commandList.SetPipeline(computePipeline);
                        commandList.SetComputeResourceSet(0, computeResourceSet);
                        commandList.Dispatch(ShaderBuiltinsComputeTest.Methods, 1, 1);
                        commandList.End();

                        graphicsDevice.SubmitCommands(commandList);
                        graphicsDevice.WaitForIdle();

                        // Read back parameters using a staging buffer
                        using (DeviceBuffer stagingBuffer =
                            factory.CreateBuffer(new BufferDescription(inOutBuffer.SizeInBytes, BufferUsage.Staging)))
                        {
                            commandList.Begin();
                            commandList.CopyBuffer(inOutBuffer, 0, stagingBuffer, 0, stagingBuffer.SizeInBytes);
                            commandList.End();
                            graphicsDevice.SubmitCommands(commandList);
                            graphicsDevice.WaitForIdle();

                            // Read back parameters
                            MappedResourceView<ComputeShaderParameters> map =
                                graphicsDevice.Map<ComputeShaderParameters>(stagingBuffer, MapMode.Read);
                            for (int i = 0; i < gpuParameters.Length; i++)
                                gpuParameters[i] = map[i];
                            graphicsDevice.Unmap(stagingBuffer);
                        }

                        /*
                         * Compare results
                         */
                        for (int method = 0; method < ShaderBuiltinsComputeTest.Methods; method++)
                        {
                            ComputeShaderParameters cpu = cpuParameters[method];
                            ComputeShaderParameters gpu = gpuParameters[method];

                            IReadOnlyCollection<Tuple<string, object, object>> deepCompareObjectFields = DeepCompareObjectFields(cpu, gpu);
                            if (deepCompareObjectFields.Count < 1) continue;

                            if (!failures.TryGetValue(method, out var methodList))
                                failures.Add(method, methodList =
                                    new List<Tuple<ComputeShaderParameters, ComputeShaderParameters,
                                        IReadOnlyCollection<Tuple<string, object, object>>>>());
                            methodList.Add(
                                new Tuple<ComputeShaderParameters, ComputeShaderParameters,
                                    IReadOnlyCollection<Tuple<string, object, object>>>(
                                    cpu, gpu, deepCompareObjectFields));
                        }

                        // Continue until we have done enough loops, or run out of time.
                        durationTicks = Stopwatch.GetTimestamp() - startTicks;
                    } while (loops++ < TestLoops &&
                             durationTicks < TestDuration.Ticks);
                }
            }

            TimeSpan testDuration = TimeSpan.FromTicks(durationTicks);
            _output.WriteLine(
                $"Executed compute shader using {toolChain.GraphicsBackend} {loops} times in {testDuration.TotalSeconds}s.");

            if (failures.Any())
            {
                _output.WriteLine($"{failures.Count} methods experienced failures out of {ShaderBuiltinsComputeTest.Methods} ({100f * failures.Count / ShaderBuiltinsComputeTest.Methods:##.##}%).  Details follow...");

                string spacer1 = new string('=', 80);
                string spacer2 = new string('-', 80);

                // Output failures in descending order.
                foreach (KeyValuePair<int, List<Tuple<ComputeShaderParameters, ComputeShaderParameters,
                    IReadOnlyCollection<Tuple<string, object, object>>>>> method in failures.OrderByDescending(kvp =>
                    kvp.Value.Count))
                {
                    int methodFailureCount = method.Value.Count;
                    _output.WriteLine(string.Empty);
                    _output.WriteLine(spacer1);
                    _output.WriteLine(
                        $"Method {method.Key} failed {methodFailureCount} times ({100f * methodFailureCount / loops:##.##}%).");

                    foreach (IGrouping<string, Tuple<float, float>> group in method.Value.SelectMany(t => t.Item3)
                        .ToLookup(f => f.Item1,
                            f => Tuple.Create((float)f.Item2, (float)f.Item3)).OrderByDescending(g => g.Count()))
                    {
                        _output.WriteLine(spacer2);
                        _output.WriteLine(string.Empty);

                        int fieldFailureCount = group.Count();
                        _output.WriteLine($"  {group.Key} failed {fieldFailureCount} times ({100f * fieldFailureCount / methodFailureCount:##.##}%)");

                        int examples = 0;
                        foreach (Tuple<float, float> tuple in group)
                        {
                            if (examples++ > 10)
                            {
                                _output.WriteLine($"    ... +{fieldFailureCount - 10} more");
                                break;
                            }

                            _output.WriteLine($"    {tuple.Item1,13} != {tuple.Item2}");
                        }
                    }
                }

                Assert.False(true, "GPU and CPU results were not identical!");
            }

            _output.WriteLine("CPU & CPU results were identical for all methods over all iterations!");
        }

        public static IReadOnlyCollection<Tuple<string, object, object>> DeepCompareObjectFields<T>(T a, T b)
        {
            // Creat failures list
            List<Tuple<string, object, object>> failures = new List<Tuple<string, object, object>>();

            // Get dictionary of fields by field name and type
            Dictionary<Type, IReadOnlyCollection<FieldInfo>> childFieldInfos =
                new Dictionary<Type, IReadOnlyCollection<FieldInfo>>();

            Type currentType = typeof(T);
            object aValue = a;
            object bValue = b;
            Stack<Tuple<string, Type, object, object>> stack = new Stack<Tuple<string, Type, object, object>>();
            stack.Push(Tuple.Create(string.Empty, currentType, aValue, bValue));

            while (stack.Count > 0)
            {
                // Pop top of stack.
                Tuple<string, Type, object, object> tuple = stack.Pop();
                currentType = tuple.Item2;
                aValue = tuple.Item3;
                bValue = tuple.Item4;

                if (Equals(aValue, bValue)) continue;

                // Get fields (cached)
                if (!childFieldInfos.TryGetValue(currentType, out IReadOnlyCollection<FieldInfo> childFields))
                    childFieldInfos.Add(currentType, childFields = currentType.GetFields().Where(f => !f.IsStatic).ToArray());

                if (childFields.Count < 1)
                {
                    // Use float tolerance check
                    if (currentType == typeof(float) &&
                        Math.Abs((float)aValue - (float)bValue) <= Tolerance)
                        continue;

                    // No child fields, we have an inequality
                    string fullName = tuple.Item1;
                    failures.Add(Tuple.Create(fullName, aValue, bValue));
                    continue;
                }

                foreach (FieldInfo childField in childFields)
                {
                    object aMemberValue = childField.GetValue(aValue);
                    object bMemberValue = childField.GetValue(bValue);

                    // Short cut equality
                    if (Equals(aMemberValue, bMemberValue)) continue;

                    string fullName = string.IsNullOrWhiteSpace(tuple.Item1)
                        ? childField.Name
                        : $"{tuple.Item1}.{childField.Name}";
                    stack.Push(Tuple.Create(fullName, childField.FieldType, aMemberValue, bMemberValue));
                }
            }

            return failures.AsReadOnly();
        }


        /// <summary>
        /// The random number generators for each thread.
        /// </summary>
        private static readonly ThreadLocal<Random> _randomGenerators =
            new ThreadLocal<Random>(() => new Random());

        /// <summary>
        /// Create a type with random data.
        /// </summary>
        /// <typeparam name="T">The random type</typeparam>
        /// <param name="size">The optional number of bytes to fill.</param>
        /// <returns></returns>
        public unsafe T GetRandom<T>(int size = default(int))
        {
            Random random = _randomGenerators.Value;
            size = Math.Min(Unsafe.SizeOf<T>(), size < 1 ? Int32.MaxValue : size);
            T result = Activator.CreateInstance<T>();
            // This buffer holds a random number
            byte[] buffer = new byte[4];
            int pi = 0;

            // Grab pointer to struct
            ref byte asRefByte = ref Unsafe.As<T, byte>(ref result);
            fixed (byte* ptr = &asRefByte)
                while (pi < size)
                {
                    int b = pi % 4;
                    if (b == 0)
                        // Update random number in buffer every 4 bytes
                        random.NextBytes(buffer);
                    *(ptr + pi++) = buffer[b];
                }

            return result;
        }

        private class ShaderSetProcessor : IShaderSetProcessor
        {
            public string Result { get; private set; }

            public string UserArgs { get; set; }

            public void ProcessShaderSet(ShaderSetProcessorInput input)
            {
                Result = string.Join(" ", input.Model.AllResources.Select(rd => rd.Name));
            }
        }
    }
}