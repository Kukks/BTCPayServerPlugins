using System.ComponentModel;
using WalletWasabi.WabiSabi.Models;

namespace WalletWasabi.Affiliation.Serialization;

public class DefaultAffiliationFlagAttribute : DefaultValueAttribute
{
	public DefaultAffiliationFlagAttribute() : base(AffiliationFlag.Default)
	{
	}
}
