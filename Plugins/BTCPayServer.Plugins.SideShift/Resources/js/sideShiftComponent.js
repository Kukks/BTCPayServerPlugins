Vue.component("side-shift", {
    props: ["toCurrency", "toCurrencyDue", "toCurrencyAddress"],
    methods: {
        openDialog: function (e) {
            if (e && e.preventDefault) {
                e.preventDefault();
            }
            const toCurrency = this.toCurrency.toLowerCase();
            let settleMethodId = "";
            let amount = !this.$parent.srvModel.isUnsetTopUp
                ? this.toCurrencyDue
                : undefined;
            if (toCurrency === "lbtc") {
                settleMethodId = "liquid";
            } else if (toCurrency=== "usdt") {
                settleMethodId = "usdtla";
            } else if (
                toCurrency.endsWith("lightninglike") ||
                toCurrency.endsWith("lnurlpay")
            ) {
                settleMethodId = "ln";
            } else {
                settleMethodId = toCurrency.replace('_btclike', '').replace('_monerolike', '').replace('_zcashlike', '').toLowerCase();
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
