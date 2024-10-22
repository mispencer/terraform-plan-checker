# terraform-plan-checker
A program to check the output of a Terraform plan in JSON format to see if it matches a policy of allowed changes or drifts.

For example: 
```
terraform plan -o myplan.tfplan
terraform show -json dev.tfplan > myplan.tfplan.json
dotnet run myplan.tfplan.json mypolicy.json
```
