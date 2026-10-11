import { Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

// Recipes: the dishes' recipe cards, the prep items made in the kitchen from their own recipes,
// and the production orders that make them.
@Component({
  selector: 'app-recipes-shell',
  imports: [MatButtonModule, MatIconModule, RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <nav class="sub-nav no-print" aria-label="Recipes">
      <!-- The dishes are everything under Recipes that is not the other tabs: their cards too. -->
      <a mat-button routerLink="." [class.active]="dishes(prep, production, categories)" [attr.aria-current]="dishes(prep, production, categories) ? 'page' : null">
        <mat-icon>restaurant_menu</mat-icon> Dishes<span class="inline-ar">الأطباق</span>
      </a>
      <a mat-button routerLink="prep" routerLinkActive="active" #prep="routerLinkActive" ariaCurrentWhenActive="page">
        <mat-icon>blender</mat-icon> Prep items<span class="inline-ar">الأصناف نصف المصنعة</span>
      </a>
      <a mat-button routerLink="production" routerLinkActive="active" #production="routerLinkActive" ariaCurrentWhenActive="page">
        <mat-icon>precision_manufacturing</mat-icon> Production orders<span class="inline-ar">أذونات التصنيع</span>
      </a>
      <a mat-button routerLink="categories" routerLinkActive="active" #categories="routerLinkActive" ariaCurrentWhenActive="page">
        <mat-icon>category</mat-icon> Categories<span class="inline-ar">أقسام الوصفات</span>
      </a>
    </nav>
    <router-outlet />
  `,
  styleUrl: './costing.scss',
  styles: `
    .sub-nav {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 8px;
      margin-bottom: 16px;

      a.active {
        background: var(--mat-sys-secondary-container);
        color: var(--mat-sys-on-secondary-container);
      }
    }
  `,
})
export class RecipesShell {
  protected dishes(...others: RouterLinkActive[]): boolean {
    return others.every((o) => !o.isActive);
  }
}
