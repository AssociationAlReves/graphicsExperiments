using Silk.NET.OpenGL;

namespace SilkNetVoronoi;

/// <summary>Shared GLSL loading/compile/link helpers used by the renderers.</summary>
internal static class GlHelpers
{
    /// <summary>
    /// Compile + link a program from two shader files in the <c>shaders/</c> folder that is
    /// copied next to the binary. File names appear in any error so failures are easy to trace.
    /// </summary>
    public static uint LinkFromFiles(GL gl, string vertFile, string fragFile)
    {
        uint vs = Compile(gl, ShaderType.VertexShader, LoadSource(vertFile), vertFile);
        uint fs = Compile(gl, ShaderType.FragmentShader, LoadSource(fragFile), fragFile);

        uint program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0)
            throw new Exception($"Link error ({vertFile} + {fragFile}): " + gl.GetProgramInfoLog(program));

        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return program;
    }

    private static string LoadSource(string fileName)
    {
        // Resolve against the binary's directory (where shaders/ is copied), not the
        // working directory, so it works under `dotnet run` and the bare executable alike.
        string path = Path.Combine(AppContext.BaseDirectory, "shaders", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Shader file not found: {path}", path);
        return File.ReadAllText(path);
    }

    private static uint Compile(GL gl, ShaderType type, string src, string name)
    {
        uint sh = gl.CreateShader(type);
        gl.ShaderSource(sh, src);
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            throw new Exception($"{type} compile error ({name}): " + gl.GetShaderInfoLog(sh));
        return sh;
    }
}
