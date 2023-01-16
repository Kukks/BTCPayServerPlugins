Vue.component("fixed-float", {
    props: ["toCurrency", "toCurrencyDue", "toCurrencyAddress"],
    data() {
        return {
            shown: false,
        };
    },
    computed: {
        url() {
            let settleMethodId = "";
            if (
                this.toCurrency.endsWith("LightningLike") ||
                this.toCurrency.endsWith("LNURLPay")
            ) {
                settleMethodId = "BTCLN";
            } else {
                settleMethodId = this.toCurrency
                    .replace("_BTCLike", "")
                    .replace("_MoneroLike", "")
                    .replace("_ZcashLike", "")
                    .toUpperCase();
            }
            const topup = this.$parent.srvModel.isUnsetTopUp;
            return (
                "https://widget.fixedfloat.com/?" +
                `to=${settleMethodId}` +
                "&lockReceive=true&ref=fkbyt39c" +
                `&address=${this.toCurrencyAddress}` +
                (topup
                    ? ""
                    : `&lockType=true&hideType=true&lockAmount=true&toAmount=${this.toCurrencyDue}`)
            );
        },
    },
});
