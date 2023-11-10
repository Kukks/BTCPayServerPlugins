# Dynamic Reports Plugin for BTCPay Server
This plugin allows you to create dynamic reports in BTCPay Server, along with re-enabling the old invoice export report.

## Usage
After installing the plugin, you will see a new menu item in the server settings called "Dynamic Reports". Clicking on this will take you to the report builder.

You can create new reports using raw sql (postgres). These reports are only viewable if you  are a server admin by default. You can change this by explicitly specifying it in the report.

## Re-enabling the old invoice export report
There is a toggle available on the report builder page to re-enable the old invoice export report. This report is available to all users on your instance.