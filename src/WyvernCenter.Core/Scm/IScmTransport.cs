namespace WyvernCenter.Core.Scm;

/// <summary>Raw access to SCM0 instances. Each instance is one EC byte (or bit-field) in the firmware.</summary>
public interface IScmTransport
{
    int Read(ScmClass c, int index);
    void Write(ScmClass c, int index, int value);
    /// <summary>Every instance of a class, ordered by index.</summary>
    IReadOnlyList<int> ReadAll(ScmClass c);
}
