using System.Collections.Immutable;
using System.ComponentModel;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.Affiliation.Serialization;

public class DefaultAffiliateServersAttribute : DefaultValueAttribute
{
	public DefaultAffiliateServersAttribute() : base(ImmutableDictionary<AffiliationFlag, string>.Empty)
	{
	}
}
