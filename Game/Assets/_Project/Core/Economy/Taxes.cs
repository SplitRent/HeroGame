namespace HeroGame.Core.Economy
{
    using HeroGame.Core.Config;
    using HeroGame.Core.Foundation;

    /// <summary>Tax computations driven by server configuration (GDD §24). Pure functions.</summary>
    public sealed class TaxPolicy
    {
        private readonly EconomyConfig _config;

        public TaxPolicy(EconomyConfig config)
        {
            _config = config;
        }

        public Money IncomeTax(Money grossWage) => grossWage.Scale(_config.IncomeTaxRate);
        public Money SalesTax(Money price) => price.Scale(_config.SalesTaxRate);
        public Money TransferTax(Money salePrice) => salePrice.Scale(_config.PropertyTransferTaxRate);
        public Money BusinessTax(Money profit) => profit.Cents <= 0 ? Money.Zero : profit.Scale(_config.BusinessTaxRate);

        /// <summary>Property tax owed for one day on an assessed value.</summary>
        public Money DailyPropertyTax(Money assessedValue) => assessedValue.Scale(_config.PropertyTaxAnnualRate / 365.0);

        public Money VehicleRegistrationFee => new Money(_config.VehicleRegistrationFeeCents);
    }
}
