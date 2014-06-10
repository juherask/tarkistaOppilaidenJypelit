# Tarkista Oppilaiden Jypelit #

Tämä Jypeli-kirjastoa käyttävä ohjelma lataa pelit versionhallinnasta (snv.exe) ja tarkistaa ne (msbuild.exe). Ohjelman käyttötarkoitus on oppilaiden Nuorten Peliohjelmointikurssilla tekemien pelien nouto ja validiteetin tarkistaminen ennen parhaan pelin arviointia.

## Käyttöohje ##

Tarkistustehtävä määritellään suoraan lähdekoodiin. Ulkoisten ohjelmien polut ja tarkastustehtävä on kovakoodattu TarkistaOppilaidenPelit.cs -tiedostoon:

```
string SVN_CLI_EXE = @"C:\Temp\svn-win32-1.8.8\bin\svn.exe";
string MSBUILD_EXE = @"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MsBuild.exe";
```
  
Haettavat pelit määritellään ```GetHardCodedList()```-aliohjelmaan. Katso esimerkki koodista. Kenttien pitäisi olla yksiselitteisiä poislukien ```toFetch```, joka on lista haettavista kansioista ja tiedostoista SVN polun alla. Tämä johtuu siitä, että ohjelma voidaan määrittää hakemaan vain ne kansiot ja tiedostot, jotka käänntettävä peli tarvii, vaikka versionhallinnassa olisi muutakin. Pellin alla käytämme svn:ää seuraavasti:

```
> svn checkout <repo> <author_folder> --depth empty
> cd <author_folder>
> svn up <files_and_folders_you_want>
```

## TODO ##
* Selvitä miten pelin heittämä poikkeus saadaan kiinni (nyt kaatuvaa peliä ei tunnisteta oikein).
* Toteuta toiminto, jolla pelit voi julkaista kurssin wikiin.

## Kuvia ##

![Pelin tila on luettavissa palluran väristä](https://raw.githubusercontent.com/juherask/tarkistaOppilaidenJypelit/master/tarkista_pelit_status.jpg)

![Pelin virhetiedot saa klikkaamalla palluraa](https://raw.githubusercontent.com/juherask/tarkistaOppilaidenJypelit/master/tarkista_pelit_error.jpg)
