using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace Platform.Identity.Tests;

/// <summary>
/// A key ring's repository over a list of <see cref="XElement"/>, which is what the cell's Parameter
/// Store ring is at the framework's seam (change request P2-2, decision 7). Every
/// <see cref="IdentityGateway"/> has one of its own unless a test passes it another, so no identity test
/// writes a key under the user's profile, and two gateways share a ring only when a test hands both
/// the same instance.
/// </summary>
internal sealed class ListXmlRepository : IXmlRepository
{
    private readonly List<XElement> elements = [];

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (elements)
        {
            return [.. elements.Select(e => new XElement(e))];
        }
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (elements)
        {
            elements.Add(new XElement(element));
        }
    }
}
