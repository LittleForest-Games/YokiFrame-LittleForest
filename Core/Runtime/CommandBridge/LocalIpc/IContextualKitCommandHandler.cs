namespace YokiFrame
{
    /// <summary>
    /// Optional handler seam for extensions that need request and transport correlation.
    /// Existing <see cref="IKitCommandHandler"/> implementations remain unchanged.
    /// </summary>
    public interface IContextualKitCommandHandler : IKitCommandHandler
    {
        string HandleCommand(CommandBridgeCommand command);
    }
}
