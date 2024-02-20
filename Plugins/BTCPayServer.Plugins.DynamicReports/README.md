# Dynamic Reports Plugin for BTCPay Server
This plugin allows you to create dynamic reports in BTCPay Server, along with re-enabling the old invoice export report.

## Usage
After installing the plugin, go to "Server Settings" -> "Dynamic Reports".

### Re-enabling the old invoice export report
There is a toggle button called "Enable legacy report" available to re-enable the old invoice export. After enabling it will be available in the "Reporting" menu alongside the other existing reports. This report will be available to all users on your instance.

### Creating custom reports
You can create new reports using raw sql (postgres). These reports are only viewable if you  are a server admin by default. You can change this by explicitly specifying it in the report.

