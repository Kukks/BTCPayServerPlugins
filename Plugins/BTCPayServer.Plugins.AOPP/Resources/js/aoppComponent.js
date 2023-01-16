Vue.component("AOPP", {
    props: ["srvModel"],
    methods: {
        onaoppChange: function(){
            this.aoppAddressInputDirty = true;
            this.aoppAddressInputInvalid = false;
        },
        onSubmit : function(){
            var self = this;
            if (this.aoppAddressInput && this.aoppAddressInput.startsWith("aopp:?")) {
                this.aoppAddressFormSubmitting = true;
                // Push the email to a server, once the reception is confirmed move on
                $.ajax({
                    url: "/plugins/"+this.srvModel.storeId+"/AOPP/" +this.srvModel.invoiceId,
                    type: "POST",
                    data: JSON.stringify({ aopp: this.aoppAddressInput }),
                    contentType: "application/json; charset=utf-8"
                })
                    .done(function () {
                    }).always(function () {
                    self.aoppAddressFormSubmitting = false;
                });
            } else {
                this.aoppAddressInputInvalid = true;
            }
        }
    },
    data: function () {
        return {
            aoppAddressInput: "",
            aoppAddressInputDirty: false,
            aoppAddressInputInvalid: false,
            aoppAddressFormSubmitting: false
        }
    }
});
