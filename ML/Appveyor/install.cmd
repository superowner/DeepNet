choco feature enable -n allowEmptyChecksums
cinst r.project --version 3.3.2
"C:\Program Files\R\R-3.3.2\bin\R.exe" -q -e "install.packages(c('plotrix', 'ggplot2'), repos='http://cran.us.r-project.org')"

