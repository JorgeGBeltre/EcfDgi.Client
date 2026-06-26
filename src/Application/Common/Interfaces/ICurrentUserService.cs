namespace EcfDgii.Client.Application.Common.Interfaces
{
    public interface ICurrentUserService
    {
        string? UserId { get; }
        string? Username { get; }
    }
}
