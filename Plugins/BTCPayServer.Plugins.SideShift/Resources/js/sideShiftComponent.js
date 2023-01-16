Vue.component("side-shift", {
    props: ["toCurrency", "toCurrencyDue", "toCurrencyAddress"],
    methods: {
        openDialog: function (e) {
            if (e && e.preventDefault) {
                e.preventDefault();
            }
            let settleMethodId = "";
            let amount = !this.$parent.srvModel.isUnsetTopUp
                ? this.toCurrencyDue
                : undefined;
            if (this.toCurrency.toLowerCase() === "lbtc") {
                settleMethodId = "liquid";
            } else if (this.toCurrency.toLowerCase() === "usdt") {
                settleMethodId = "usdtla";
            } else if (
                this.toCurrency.endsWith("LightningLike") ||
                this.toCurrency.endsWith("LNURLPay")
            ) {
                settleMethodId = "ln";
            } else {
                settleMethodId = this.toCurrency
                    .replace("_BTCLike", "")
                    .replace("_MoneroLike", "")
                    .replace("_ZcashLike", "")
                    .toLowerCase();
            }
            window.__SIDESHIFT__ = {
                parentAffiliateId: "qg0OrfHJV",
                defaultSettleMethodId: settleMethodId,
                settleAddress: this.toCurrencyAddress,
                settleAmount: amount,
                type: !this.$parent.srvModel.isUnsetTopUp ? "fixed" : undefined,
            };
            window.sideshift.show();
        },
    },
});
