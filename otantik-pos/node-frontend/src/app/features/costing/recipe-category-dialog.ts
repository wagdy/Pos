import { Component, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { firstValueFrom } from 'rxjs';
import { CostingApi, RecipeCategory } from '../../core/costing/costing.api';
import { errorMessage } from '../../core/http/error-message';

// A category to rename, or none for a new one; fromRecipe when added from a recipe's form.
export interface RecipeCategoryDialogData {
  category?: RecipeCategory;
  fromRecipe?: boolean;
}

// A recipe category's name, in English and optionally Arabic. Closes with the category as the
// till saved it; a refusal (the name already taken) keeps it open with all typed.
@Component({
  selector: 'app-recipe-category-dialog',
  imports: [MatButtonModule, MatDialogModule, MatFormFieldModule, MatInputModule, ReactiveFormsModule],
  template: `
    <h2 mat-dialog-title>
      @if (data.category) {
        Rename category <span class="ar">تعديل القسم</span>
      } @else {
        Add a recipe category <span class="ar">إضافة قسم</span>
      }
    </h2>
    <mat-dialog-content>
      @if (data.fromRecipe) {
        <p class="hint">Added now, the recipe goes into it.</p>
      }
      <form [formGroup]="form" (ngSubmit)="save()" id="recipe-category-form" class="form">
        <mat-form-field>
          <mat-label>Name · الاسم</mat-label>
          <input matInput formControlName="name" maxlength="100" placeholder="Sauces" cdkFocusInitial />
        </mat-form-field>
        <mat-form-field>
          <mat-label>Arabic name · الاسم بالعربية</mat-label>
          <input matInput formControlName="nameAr" maxlength="100" dir="rtl" placeholder="الصوصات" />
        </mat-form-field>
      </form>
      @if (problem(); as p) {
        <p class="error">{{ p }}</p>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close [disabled]="saving()">Cancel</button>
      <button mat-flat-button type="submit" form="recipe-category-form" [disabled]="form.invalid || saving()">
        {{ data.fromRecipe ? 'Add and use it' : 'Save' }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .form {
      display: grid;
      gap: 4px;
      min-width: min(380px, 80vw);
    }
    .ar {
      direction: rtl;
      unicode-bidi: isolate;
      margin-left: 6px;
      opacity: 0.75;
      font-size: 0.8em;
    }
    .hint {
      margin: 0 0 12px;
      color: var(--mat-sys-on-surface-variant);
    }
    .error {
      color: var(--mat-sys-error);
    }
  `,
})
export class RecipeCategoryDialog {
  private readonly api = inject(CostingApi);
  private readonly ref = inject(MatDialogRef<RecipeCategoryDialog, RecipeCategory>);
  protected readonly data = inject<RecipeCategoryDialogData>(MAT_DIALOG_DATA);

  protected readonly saving = signal(false);
  protected readonly problem = signal<string | null>(null);

  protected readonly form = new FormGroup({
    name: new FormControl(this.data.category?.name ?? '', { nonNullable: true, validators: [Validators.required, Validators.pattern(/\S/)] }),
    nameAr: new FormControl(this.data.category?.nameAr ?? '', { nonNullable: true }),
  });

  // The id is made here for a new one: a retry after a lost answer adds it once.
  private readonly id = this.data.category?.id ?? crypto.randomUUID();

  protected async save(): Promise<void> {
    if (this.form.invalid || this.saving()) {
      return;
    }
    const v = this.form.getRawValue();
    this.saving.set(true);
    this.problem.set(null);
    try {
      this.ref.close(await firstValueFrom(this.api.saveRecipeCategory(this.id, { name: v.name.trim(), nameAr: v.nameAr.trim() || null })));
    } catch (error) {
      this.problem.set(errorMessage(error, 'The category could not be saved.'));
    } finally {
      this.saving.set(false);
    }
  }
}
