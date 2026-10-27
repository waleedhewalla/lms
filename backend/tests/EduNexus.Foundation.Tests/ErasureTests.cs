using EduNexus.Foundation;

namespace EduNexus.Foundation.Tests;

public class ErasureTests
{
    [Fact]
    public void Pseudonym_Is_Stable_And_Non_Identifying()
    {
        var id = Guid.Parse("0dc98ebb-1234-4abc-9def-001122334455");
        Assert.Equal("Former member 0DC98EBB", Erasure.PseudonymFor(id));
        Assert.Equal(Erasure.PseudonymFor(id), Erasure.PseudonymFor(id));
    }

    [Fact]
    public void Pseudonymise_Removes_Identity_But_Keeps_The_Record()
    {
        var p = new Person(Guid.NewGuid(), Guid.NewGuid(), PersonType.Employee, "Huda Saleh", "huda@example.test", true,
            DateTimeOffset.UtcNow, Guid.NewGuid(), "Professor");
        var e = Erasure.Pseudonymise(p);
        Assert.Equal(p.Id, e.Id);
        Assert.Equal(p.DepartmentId, e.DepartmentId);
        Assert.StartsWith("Former member ", e.FullName);
        Assert.Null(e.Email);
        Assert.Null(e.AcademicRank);
        Assert.False(e.IsActive);
    }
}
